# Call interface

**Status:** design of 2026-10-06, the outcome of the second design review. The signatures below fix the shape of the interface; milestone 1 will settle exact names. Numbers marked "to be measured" are decided by the experiments in the [README](README.md#milestone-1), not by this document.

This document describes how a gRPC call moves between a host, the GRPC.NET core and a service method. It builds on the transport decisions in the [README](README.md#transport): the core is transport-agnostic, and Kestrel hosting over HTTP/2 and HTTP/3 ships out of the box. A built-in HTTP/2 host is planned from the start and built third, because the review concluded that it is where the project's throughput premise is decided.

## Design rules

The project exists only to unlock significant throughput gains over grpc-dotnet. Every rule below follows from that.

1. **No hop the host does not force.** Kestrel dispatches each HTTP/2 stream to the thread pool, and nothing in GRPC.NET adds a second hop. A built-in host runs synchronous methods on the receive thread.
2. **No copy the core does not need.** Request bodies are decoded in place when contiguous. Responses are written straight into the host's buffer, never staged and copied.
3. **The core never flushes.** Hosts flush once per read pass, so many responses leave in one write.
4. **Nothing is allocated for features a call does not use.** No metadata collection, timer, cancellation source or channel unless the call needs one.
5. **Every claim is measured.** Each rule has an experiment or a CI gate attached (see [Milestone 1](README.md#milestone-1)).

## What one gRPC call carries

The [gRPC over HTTP/2 specification](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) allows exactly this sequence on one call:

1. **Request headers**, first: the method path (`/package.Service/Method`), the deadline in `grpc-timeout`, `content-type`, `grpc-encoding`, and any custom metadata.
2. **Request messages**: zero or more. Each message carries gRPC's own 5-byte prefix, a 1-byte compressed flag followed by a 4-byte big-endian length.
3. **Response headers**: optional initial metadata, which must precede the first response message.
4. **Response messages**: zero or more, with the same prefix.
5. **Trailers**, last: `grpc-status`, `grpc-message` and trailing metadata.

A call that fails before writing any message may send a **trailers-only** response: one header block that carries the status and ends the stream.

The call has two lanes. The **data lane** is two ordered byte streams with gRPC's framing. The **control lane** carries metadata at three fixed points (request headers, response headers, trailers) and two signals (cancellation and deadline expiry). Metadata is therefore a call context with boundary events, not a channel.

## Layers

| Layer | Owns | Does not own |
| --- | --- | --- |
| **Host** (the Kestrel package, the built-in host, or an application's own transport) | Connections, HTTP/2 and HTTP/3 framing, HPACK, flow control, flushing, connection limits, parsing `grpc-timeout`, and turning stream resets into cancellation | Message framing, method lookup, serialization |
| **Core** | Method lookup, the 5-byte framing, size limits, the codec, invocation, deadline checks, mapping exceptions to status, and the order of headers, messages and trailers | Sockets, HTTP, ASP.NET Core, flushing |
| **Method view** | Typed requests, responses, readers and writers, metadata and cancellation | Bytes and framing |

## Sketch

```csharp
// Everything the host knows when a call starts. No owned object per call.
public readonly struct CallHead
{
    public MethodId Method { get; }            // from Resolve, or bound at registration on Kestrel
    public long DeadlineTicks { get; }         // absolute, monotonic (Environment.TickCount64 scale); 0 = none
    public HostCallState? State { get; }       // host-owned, pooled per stream: cancellation, the write hook, reader/writer reuse
}

// Implemented by each host, pooled with its streams. Null for a synchronous unary call.
public abstract class HostCallState
{
    public abstract CancellationToken Cancellation { get; }
    // Called on the writing thread after each response message is advanced. The host flushes, keeps buffering, or
    // returns a pending task until its own count of bytes actually sent is under its per-stream bound.
    protected abstract ValueTask OnMessageWritten(PipeWriter output, int framedBytes);
}

public readonly struct GrpcStatus
{
    public StatusCode Code { get; }
    public ReadOnlyMemory<byte> Message { get; }   // UTF-8; empty for OK; percent-encoded on the wire by the host
}

// Implemented by each host as a struct, bound generically: no interface call per event.
public interface ICallSink
{
    void WriteHeaders(in MetadataView responseHeaders);              // only when custom headers exist or a stream begins
    void Complete(in GrpcStatus status, in MetadataView trailers);   // hands the writer an immutable descriptor
}

public sealed class GrpcProcessor
{
    // A fixed table keyed by UTF-8 bytes, built at startup. Never allocates.
    public MethodId Resolve(ReadOnlySpan<byte> pathUtf8);

    // Unary, synchronous: the primitive. The method runs, then the response is sized and written.
    public GrpcStatus Process<TSink>(in CallHead head, ReadOnlySpan<byte> body,
                                     IBufferWriter<byte> output, ref TSink sink)
        where TSink : struct, ICallSink;

    // Same, for a body that may span segments: IsSingleSegment ? FirstSpan : one copy into per-thread scratch.
    public GrpcStatus Process<TSink>(in CallHead head, in ReadOnlySequence<byte> body,
                                     IBufferWriter<byte> output, ref TSink sink)
        where TSink : struct, ICallSink;

    // Async and streaming calls: byte pipes for data, the sink for boundaries.
    public ValueTask ProcessAsync<TSink>(CallHead head, PipeReader requestBody, PipeWriter responseBody, TSink sink)
        where TSink : struct, ICallSink;
}
```

`Process` guarantees what the hosts rely on: it is re-entrant, never flushes, never awaits, and writes into whatever `IBufferWriter<byte>` the host passes. On the common path, a contiguous request body and a response written as one span, it allocates nothing beyond what the codec allocates for the messages. Two paths use buffers, and E0 reports their cost separately: a split request body is copied into per-thread scratch (allocated once per thread) or, when that is busy or too small, a pooled buffer; a response above the host's contiguous cap is staged in a pooled buffer (see [The request path](#the-request-path) and [The response path](#the-response-path)). On the built-in host that writer is the connection's send buffer itself.

There is no batch API. The host's read loop is the batch: it calls `Process` for every complete request in a read pass, then flushes once.

## Where methods run

**Behind Kestrel.** Kestrel's dispatch of each stream to the thread pool is fixed ([Http2Connection.cs#L1380-L1389](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L1380-L1389)). The Kestrel host calls `Process` synchronously on that thread, inside the read completion when the body is complete. There is no `Task.Run`, no channel and no await between the read and `Process`. An async method finishes inline if its `ValueTask` has already completed; otherwise it pays one continuation.

**On the built-in host.** The method's signature decides, under a host policy:

| Declaration | Runs |
| --- | --- |
| `TResp M(in TReq)` or `void M(in TView, ref TWriter)` | Inline on the receive thread. A synchronous signature promises to return without waiting, the contract Garnet and pajamax impose on their handlers. |
| `ValueTask<TResp> M(...)` | Starts inline; finishes inline if already completed; otherwise its continuation runs on the thread pool. A reactor-thread model, as in ioxide, is a later experiment. |
| `[GrpcMethod(Dispatch = true)]` on a synchronous method | Always on the thread pool, for a method known to be slow or blocking. |

- **`InlinePolicy`.** `SyncMethods` runs every synchronous method inline. `OptInOnly` runs inline only methods marked `[GrpcMethod(Inline = true)]`.
- **Demotion, sampled.** Under `SyncMethods` the host times about 1 in 16 calls per method, chosen by a per-method counter, so unsampled calls never read the clock. A method is switched to dispatch, with a warning logged, when more than 1% of its last 1,024 sampled calls exceed the inline budget (default 100 µs). This bounds the stall a misbehaving method causes without asking every author to annotate. If experiment E5 shows the sampled monitor costing more than 1% of CPU per call, it is off by default and becomes an opt-in diagnostic.
- **The shipped default is decided by measurement.** E5 mixes 1% of calls hitting a 1 ms synchronous method at 256 calls in flight. If fast-call p99 under `SyncMethods` (with demotion, if it stays on by default) is more than 2× its p99 under `OptInOnly`, the default is `OptInOnly`. Otherwise it is `SyncMethods`.
- **Inline socket completions.** User methods run on the socket engine's epoll threads only when a deployment explicitly combines `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` with `SyncMethods`. That combination is the Garnet model and never a default.

## The request path

- **Prefix.** The core reads the 5-byte prefix in place, copying it to a stack buffer only when it straddles segments. A length above the limit is rejected before any scratch is rented.
- **Body.** A single-segment body is decoded from `FirstSpan`. A split body is copied once into per-thread scratch, bounded by the maximum message size, and decoded from there. Benchmarks report both paths; that the extra branch costs nothing is something to measure, not assume.
- **Built-in host buffers.** The built-in host receives into pinned, pooled per-connection buffers, so most bodies arrive contiguous and call the span primitive directly.

## The response path

**Size first, then one contiguous write.**

```
size = codec.Size(in response)
span = output.GetSpan(5 + size)
span[0] = 0; big-endian size into span[1..5]; codec.Encode(in response, size, span[5..])
output.Advance(5 + size)
```

- No staging buffer and no copy on the common path. JSON-RPC.NET renders into scratch and copies because a JSON error can occur mid-document; gRPC's unary method has already returned before encoding starts.
- An exception inside `Encode` leaves nothing committed, because the bytes were never advanced. The host sends trailers-only `INTERNAL`.
- **Large messages.** A message larger than the host's contiguous-span cap, such as the interop suite's 314,159-byte `large_unary` response, either goes through a staging buffer copied in chunks, or is advanced frame by frame with a stream reset on an exception. Each host chooses and documents one.
- A DATA frame that was partly published is never followed by a success status. The stream is reset.

**Flushing.** The core never flushes. Hosts flush:

- at the end of each read pass;
- when a method's `ValueTask` returns pending to the host loop, which the host can observe;
- for a response that completes outside a read pass, on a zero-delay writer turn that drains everything ready;
- immediately when exactly one response is ready and the queue is empty;
- when the send buffer is full.

No byte or message thresholds are fixed in advance; a sweep (immediate, pass end, 16, 32 and 64 KiB) decides them, gated by no p99 regression at equal offered load. Unary calls on Kestrel never call `FlushAsync`: the response completes, and Kestrel's frame writer emits HEADERS, DATA and trailers in one pass.

For streams the host makes these decisions in `OnMessageWritten`, which the core calls on the writing thread after each response message. A `PipeWriter` is not thread-safe, so a flush must happen in a turn that owns the writer; that hook is such a turn, and it keeps every flush decision in host code. It is also where the host applies its per-stream bound on pending bytes, counted as bytes actually sent: `UnflushedBytes` cannot measure that, and not every writer supports it.

**Batching across streams.**

- **Kestrel** already coalesces frames from all streams in its single frame-writer loop. GRPC.NET must not defeat it with a flush per message.
- **The built-in host** has one writer per connection that owns HPACK, both flow-control windows and frame order. Responses completed inline during a read pass are written straight into the send buffer. Responses that complete elsewhere are queued for the writer, which drains everything ready, makes one send, and keeps one write in flight while the next buffer fills. There is no TsavoriteLog-style concurrent reservation of the send buffer: HPACK and window decisions are ordered per connection, which a lock-free tail cannot express.
- **HPACK on the built-in host** uses the dynamic table on that single writer, which needs no lock. The alternative to measure is complete precomputed header blocks of literals without indexing. The purpose-built servers in HttpArena's gRPC run send 63 to 133 bytes per response against grpc-dotnet's 37, which suggests they index less, though only one of them documents how it encodes. The choice is made on CPU and bytes per call.

## Status and errors

- Before any response byte: a trailers-only header block with the mapped status.
- After complete messages: trailers with a non-OK status.
- On Kestrel, `grpc-status` values come from a static array of the 17 code strings, and `grpc-message` is set only when non-empty.
- On the built-in host, the bytes of every status code and value are precomputed, along with static-table templates. The connection writer encodes each header block with its current HPACK state, because a complete block depends on the connection's dynamic table and on ordering.

| Thrown | Status |
| --- | --- |
| `GrpcException(code, message)` | its code |
| `OperationCanceledException`, deadline passed | `DEADLINE_EXCEEDED` |
| `OperationCanceledException`, otherwise | `CANCELLED` |
| `GrpcCodecException` | `INTERNAL` |
| anything else | `UNKNOWN`, with no message unless detailed errors are enabled |

A `GrpcException` with a code and no message costs only the exception. grpc-dotnet's error path is about 4× slower than its success path ([grpc-dotnet#2033](https://github.com/grpc/grpc-dotnet/issues/2033)); that is the number to beat. Rich status details are not in milestone 1.

## Metadata

- **Synchronous methods** receive a `ref struct MetadataView` scoped to the call. It cannot escape, and the compiler enforces that.
- **Async and streaming methods** receive a host-owned `MetadataHandle`, valid for the stream, with the same lookups. It costs nothing on a call without metadata.
- **Lookups** come in two forms: `TryGet(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)`, free on the built-in host, and `TryGet(string name, out string value)`, free on Kestrel, which has already made strings. Using the other form costs one documented copy. Kestrel strings are never converted to bytes, nor raw bytes to strings, just to satisfy the core.
- **Binary headers.** `TryGetBinary(name, Span<byte> dest, out int written)` decodes `-bin` values lazily, accepts padded and unpadded base64, and splits comma-joined values.
- **Keeping metadata.** Only `ToOwned()` copies. Nothing else may be kept.

## Deadlines and cancellation

- **Deadline.** The host parses `grpc-timeout` bytes into `CallHead.DeadlineTicks`, with no `DateTime` or `TimeSpan`.
- **Synchronous methods.** The core compares ticks before invoking, and returns trailers-only `DEADLINE_EXCEEDED` if the deadline has passed. It compares once more after the method returns and before any response byte is committed, so a success is never sent after the deadline. Cost: two comparisons.
- **Async methods.** A call with no deadline and no `CancellationToken` parameter gets no timer and no cancellation source. Calls with deadlines are tracked by one shared timer wheel per host, which marks the stream and completes it with `DEADLINE_EXCEEDED`. A `CancellationTokenSource` is created lazily, only for a method that declares a token. On Kestrel it is linked with `RequestAborted` only when a deadline exists.
- **One state.** Stream reset, disconnection and deadline expiry converge on one idempotent per-stream cancellation state, so races resolve to a single status.

## Streams

```csharp
// Handles for one call: each holds the call's stamp and throws once that call has ended,
// even after the host has reused the underlying reader or writer for a later call.
public readonly struct MessageReader<T>
{
    public ValueTask<bool> MoveNextAsync();
    public ref readonly T Current { get; }     // an owned message: stays valid after later reads
}

public readonly struct MessageWriter<T>
{
    public ValueTask WriteAsync(in T message);  // sizes, encodes, advances, then calls the host's OnMessageWritten; never flushes
}
```

- One reader and one writer per stream, kept in the host's `HostCallState` and reused by the next call on that stream when the method's message and codec types match. A synchronous read completes without allocating. The reader implements `IValueTaskSource` for the pending path. The writer has no task source of its own: it waits only on the host's `OnMessageWritten`, and returns that task. Without a `HostCallState`, the reader and writer are allocated per call. E0 reports the pooled, the unpooled and a mixed-method row.
- No `System.Threading.Channels` and no `Channel<T>` per call.
- **Advancing.** Messages are owned in milestone 1, so the reader advances the pipe as soon as a message is decoded, which returns flow-control credit early. The borrowed views that come with the generated codec will need the bytes until the next `MoveNextAsync`, and will advance only then.
- **Writes are sequential.** A `WriteAsync` started before the previous one completed throws; frames stay in order without a lock.
- **Exactly one request.** For unary and server-streaming methods the core reads the request message and the end of the request stream before the handler runs. Zero or two messages end the call `INTERNAL`, and the handler never runs.
- **Faults the handler cannot hide.** The core latches the first terminal fault of a call: a malformed or truncated frame, an unsupported compressed flag or an oversized message on the request stream; a deadline seen at a write; a codec failure; a failure of the host's `OnMessageWritten`. Later reads and writes rethrow it, and the call ends with it even if the handler catches the exception and returns. The call's one status is decided in this order: the latched fault; then a deadline or cancellation seen when the handler ends; then the handler's own outcome.
- Borrowed views of streamed messages come with the generated codec, in a synchronous `OnMessage(in TView)` callback valid until it returns.

**Backpressure** never blocks a thread.

- **Kestrel** already applies stream and connection windows asynchronously in its output producer; nothing is added.
- **The built-in host's writer** segments DATA to the smaller of the stream window, the connection window and the maximum frame size. It releases credit as frames leave, and the stream awaits more credit while other streams advance. A message never has to fit the current window, which would deadlock on any message larger than the 65,535-byte initial window.
- Each stream has a bounded queue of pending bytes (64 KiB by default, to be measured). Beyond it, `WriteAsync` stays pending; the connection's send buffer is never held.
- The writer skips window-stalled streams and keeps order within each stream.
- On the receive side, a stream's `WINDOW_UPDATE` is sent only after its reader has consumed the bytes, so a slow method throttles only its own stream.

## Service registration

Registration keeps JSON-RPC.NET's model, plain lambdas and attribute-marked classes, but checks every binding against the gRPC contract in the `.proto`. As much as possible is checked at compile time; the rest is checked at startup, before the host accepts a call.

**Typed method objects.** A small generator emits one object per method in the `.proto`. The type of that object carries the request type, the response type and the call kind:

```csharp
// generated from calculator.proto (names illustrative)
public static partial class Calculator
{
    public static readonly UnaryMethod<AddRequest, AddReply> Add = new(CalculatorReflection.Descriptor, "calc.Calculator", "Add");
    public static readonly DuplexMethod<ChatMessage, ChatMessage> Chat = new(CalculatorReflection.Descriptor, "calc.Calculator", "Chat");
}
```

Each object also carries the method's path as precomputed UTF-8 bytes, its Google.Protobuf `MethodDescriptor`, and the codec binding. Registration therefore needs no strings. The constructor finds the method in the file's descriptor by its service and method names, never by position, and checks its request type, response type and call kind there. A generated file that has fallen behind its `.proto`, even one where two methods with the same signature have swapped places, fails when the object is created.

**Binding a lambda.** `Bind` has one overload per call kind; an asynchronous unary handler uses `BindAsync`, with or without the cancellation token, so the synchronous form stays visible at the call site. A handler returning the wrong type is reported as CS0411 (type arguments cannot be inferred): C# infers the response type from both the method object and the lambda, and has no way to exclude the lambda. The compiler infers the request and response types from the first argument, then types the lambda's parameters from them, so the editor offers full type hints:

```csharp
app.MapGrpcNet()
   .Bind(Calculator.Add, r => new AddReply { Sum = r.A + r.B })        // r : AddRequest
   .BindAsync(Calculator.Lookup, async r => await store.FindAsync(r.Id))
   .Bind(Calculator.Chat, async (requests, responses, ct) =>            // MessageReader<ChatMessage>, MessageWriter<ChatMessage>
   {
       while (await requests.MoveNextAsync())
           await responses.WriteAsync(requests.Current);
   });
```

```csharp
// library overloads (illustrative)
Bind<TReq, TResp>(UnaryMethod<TReq, TResp> method, Func<TReq, TResp> handler);
BindAsync<TReq, TResp>(UnaryMethod<TReq, TResp> method, Func<TReq, ValueTask<TResp>> handler);
BindAsync<TReq, TResp>(UnaryMethod<TReq, TResp> method, Func<TReq, CancellationToken, ValueTask<TResp>> handler);
Bind<TReq, TResp>(ServerStreamingMethod<TReq, TResp> method, Func<TReq, MessageWriter<TResp>, CancellationToken, ValueTask> handler);
Bind<TReq, TResp>(ClientStreamingMethod<TReq, TResp> method, Func<MessageReader<TReq>, CancellationToken, ValueTask<TResp>> handler);
Bind<TReq, TResp>(DuplexMethod<TReq, TResp> method, Func<MessageReader<TReq>, MessageWriter<TResp>, CancellationToken, ValueTask> handler);
```

A wrong request or response type, or a handler of the wrong call kind, does not compile, and there is no method-name string to misspell. The exact form `Bind<Calculator.Add>(...)` is not possible in current C#, which cannot infer the remaining type arguments from a type argument; passing the method as a value gets the same result.

**Binding a whole service.** The generator also emits a binder per service whose members are C# `required` properties, so leaving a method unbound is a compile error:

```csharp
app.MapGrpcNet(new Calculator.Binder
{
    Add  = Calculator.Add.Sync(r => new AddReply { Sum = r.A + r.B }),
    Find = Calculator.Find.Async(async (r, ct) => await store.FindAsync(r.Id, ct)),
    Chat = async (requests, responses, ct) => { /* ... */ },
});
```

Single-method `Bind` is for services implemented only in part. An unbound method answers `UNIMPLEMENTED`. A binder's unary member is a `UnaryHandler<TRequest, TResponse>` made with the method object's `Sync` or `Async` factory (the latter with or without the cancellation token): a required member has one type, so it cannot be either of two delegate types, and a factory on the already-typed method object types the lambda without inference, so a wrong type is a plain conversion error. The form is chosen once, when the binder binds; nothing is added per call. At registration the binder refuses an empty handler and a handler made from another method object, even one with the same types. Streaming members are plain delegates, since each streaming kind has one form.

**Attribute-marked classes.** The JSON-RPC.NET style stays:

```csharp
[GrpcService(typeof(Calculator))]
public sealed class CalculatorService
{
    [GrpcMethod] public AddReply Add(AddRequest request) => new() { Sum = request.A + request.B };
}
```

- Methods are matched to the `.proto` by name and checked at startup.
- If the generator is built as a Roslyn generator, an analyzer can report a mismatch at build time instead.
- Classes are registered through dependency injection, as in JSON-RPC.NET (`AddGrpcNetService<T>()`).

**Startup checks.** Every binding, whichever form it takes, is checked against the `.proto`'s service descriptor before the host starts listening:

1. The method exists in the descriptor.
2. The handler's request and response types equal the descriptor's `InputType.ClrType` and `OutputType.ClrType`.
3. The call kind agrees with `IsClientStreaming` and `IsServerStreaming`.
4. No method is bound twice.
5. Every method of a service mapped with `MapGrpcNet(binder)` is bound. Services bound one method at a time may opt out.

A failed check stops startup with the method's full name and the mismatch. The same checks are exposed for tests, so CI catches drift without starting a server. For typed bindings these checks repeat what the compiler already enforced. They remain as the backstop for attribute classes and for a generated file that has fallen behind its `.proto`.

**Execution options.** `Dispatch` or `Inline` (see [Where methods run](#where-methods-run)) goes on the lambda as an attribute, or as an argument to `Bind`.

**Cost on the hot path.** Checks run once, at registration. A naive design pays two indirect calls per request: one from the method table to the method's invoker, and one through the handler's delegate. The generator removes both wherever the target is visible at compile time.

- **One generated invoker struct per method**, such as `Calculator.AddInvoker`. Message types are classes, so a single generic invoker over `<AddRequest, AddReply>` would get shared generic code: one call site for every method in the application. A distinct struct per method gets its own compiled code.
- **Direct calls when the target is known.**
  - For attribute classes, the invoker calls `_service.Add(request)` on a sealed class. That is a direct call the JIT can inline.
  - For a method group, as in `Bind(Calculator.Add, CalcImpl.Add)`, an interceptor (the source-generator feature ASP.NET Core's minimal-API generator uses) sees the named target and emits a direct call. No delegate remains.
- **A generated dispatcher.** For services bound in the application's own compilation, the generator can emit a `switch (methodId)` that calls each invoker directly. A jump-table branch replaces the indirect call, and the JIT can inline decoding, the handler and encoding as one body. Services from other assemblies use the method table.
- **Lambdas** keep their delegate. Dynamic PGO profiles delegate call sites and emits a guarded direct call, inlining the target when one target dominates; because each method has its own invoker, each lambda has its own call site. Native AOT has no dynamic PGO, so lambdas stay indirect there, while classes and method groups stay direct. Copying a lambda's source into generated code is rejected: it breaks on captures, `using` aliases and other context local to the user's file.
- **Expected size of the gain.** An indirect call costs on the order of 1 to 2 ns, against a per-call budget of a few microseconds on a fast host, so the direct benefit is well under 0.1%. The real gain is that small methods inline into the decode and encode path.
- **Measured before it is kept.** The per-method invokers and direct calls are adopted; they are nearly free once the generator exists. The `switch` dispatcher is kept only if E0 shows a gain beyond run noise. E0 compares nanoseconds per call for a plain delegate, a delegate devirtualized by PGO, a direct call, and the generated `switch`.
- **Allocations.** A non-capturing lambda allocates nothing; a capturing one allocates once, at bind time.

**Lifetimes and sessions.**
- Services are singletons by default. Scoped and transient lifetimes, resolved per call as in JSON-RPC.NET, are an opt-in; E0 measures what the per-call scope costs.
- JSON-RPC.NET's named sessions are not carried over in milestone 1. Mapping several endpoints, each with its own set of services, covers multi-tenant hosts.

**The generator.** Grpc's own C# plugin generates method objects too, but they depend on Grpc.Core.Api, which conflicts with the minimal-dependency goal. GRPC.NET therefore emits its own. Two forms were considered: a `protoc` plugin run through Grpc.Tools' extra protoc arguments, and a Roslyn source generator that reads the descriptor `protoc` already embeds in its generated C#.

Decided: a Roslyn incremental generator, `GrpcNet.Generator`.
- **Why not a plugin.** Grpc.Tools 2.84 adds only the outputs it predicts (`X.cs` and `XGrpc.cs`) to the compilation, so a plugin's files would need their own build wiring. The interceptors, the `switch` dispatcher and the attribute-class checks need the application's C# anyway, which only Roslyn sees, so a plugin would be a second generator rather than an alternative.
- **What it reads.** Each file's reflection class holds the serialized `FileDescriptorProto` as string literals. Its `FromGeneratedCode` call lists the C# type of every message in descriptor order, and the reflection class of every import. A message of the same file maps through that list; an imported one is looked up in its import's C# namespace and must be a protobuf message. The generator repeats none of `protoc`'s naming rules for messages it can see.
- **No Google.Protobuf in the compiler.** A small reader decodes only the descriptor fields the generator uses, so the generator never loads a second copy of Google.Protobuf next to the application's. Malformed input is reported as a diagnostic (GN0001 to GN0003, GN0099), never a crash.
- **What it emits.** Per service, a `static partial class` holding one method object per method and a `Binder` with a `required` handler per method. It merges with the class Grpc's plugin generates for the same service when both run. A clash with existing code is reported as GN0003 instead of a compile error inside generated code. The binder first forces the class's static initializer, so a method object that has fallen behind its `.proto` throws its own exception at registration, not a `TypeInitializationException`.
- **Still open.** The package-consumer path (the generator inside the GrpcNet NuGet package) is untested until packaging; milestone 1 tests the analyzer project reference. Only the compiler of the SDK in `global.json` is tested.

This generator emits method objects, binders and invokers only; the generated protobuf codec remains a later, measured decision.

## Messages and the codec

```csharp
public interface IMessageCodec<T>
{
    T Decode(ReadOnlySpan<byte> payload);                  // throws GrpcCodecException
    int Size(in T message);                                // exact payload size
    void Encode(in T message, int size, Span<byte> dest);  // dest.Length == size
}
```

- **Milestone 1** uses Google.Protobuf with `protoc`-generated messages through this seam: `ParseFrom` on a span, `CalculateSize`, and `WriteTo` into exactly `size` bytes. The adapter adds no wrapper or context object per call, but Google.Protobuf still allocates the message and its strings, `bytes` and repeated fields; those allocations are measured for each message shape. The codec is deliberately not optimized in milestone 1, so the host experiments measure the host. It also keeps the benchmark control of using the same codec on both sides.
- **Invokers.** Each method binds a closed `TCodec : struct` and a generated per-method `TInvoker : struct` once, into the method table: no reflection, no delegate allocation and no generic discovery per call, as in Tsavorite's struct callbacks. See [Service registration](#service-registration) for how direct calls and the generated dispatcher remove the indirect calls.
- **Contracts.** Services are implemented in C#, as lambdas or attribute-marked classes, and bound to typed method objects generated from the `.proto` (see [Service registration](#service-registration)). The method table is built at startup. Messages are defined in `.proto`, the only wire contract, which foreign clients need anyway. A C#-first message schema is not planned: it would remove no hot-path work and double the conformance work.
- **Ownership.** Messages own their data by default. Borrowed `ref struct` views exist only in a synchronous fast profile, `void M(in TView request, ref TWriter response)` or `TResp M(in TView request)`, and arrive with the generated codec. Message objects are not pooled.
- **The generated codec** comes after milestone 1, generated from `.proto`, and only if profiling shows that, on any representative message shape, either:
  - the codec takes at least 15% of server CPU per call; or
  - the message and field allocations cost at least 3% of throughput in gen0 GC. These are the allocations the generated codec exists to remove; milestone 1's allocation gate excludes them and covers only the core's own allocations.

  It is kept only where it improves end-to-end CPU per call beyond run noise. It is tested by differential encoding and decoding against Google.Protobuf.

## Call types

| Call type | Request messages | Response messages | Core entry point |
| --- | --- | --- | --- |
| Unary | 1 | 1 | `Process` when the request body is complete; otherwise `ProcessAsync` |
| Server streaming | 1 | 0 or more | `ProcessAsync` |
| Client streaming | 0 or more | 1 | `ProcessAsync` |
| Bidirectional streaming | 0 or more | 0 or more | `ProcessAsync` |

The wire does not say which kind a call is; the method's definition does. The host resolves the method first, through `Resolve` or a route bound at registration.

## The Kestrel host

| gRPC concept | ASP.NET Core |
| --- | --- |
| Method | a route bound per method at registration, giving the `MethodId` without transcoding the path |
| Request metadata | `HttpRequest.Headers`, behind `MetadataView` or `MetadataHandle` |
| Deadline | the `grpc-timeout` request header, parsed into ticks |
| Cancellation | `HttpContext.RequestAborted`, linked with the deadline only for methods that take a token |
| Request body | `HttpRequest.BodyReader` |
| Response body | `HttpResponse.BodyWriter` |
| Response headers | `HttpResponse.Headers`, then `HttpResponse.StartAsync` |
| Trailers | `HttpResponse.AppendTrailer`, with `grpc-status` strings from a static table |

## The built-in host

Built third, after the core and the Kestrel host. The raw-framing diagnostic (experiment E3) comes before it. If E3 shows less than 2× grpc-dotnet in unary calls per server CPU at 256 calls in flight, in both socket-completion modes, the decision to build the HTTP/2 host goes to the owner with the numbers.

- h2c first, on `Socket.ReceiveAsync` and `SendAsync`, which run on a cached `SocketAsyncEventArgs` without allocating per call.
- Pinned, pooled receive and send buffers.
- One reader and one writer per connection; the writer owns HPACK and both windows.
- gRPC-only HTTP/2: no PRIORITY, no server push, no HTTP/1 upgrade.
- From the first commit: `h2spec --strict`, a cap on header-block bytes, a stream-reset rate limit against Rapid Reset, and a CONTINUATION limit.
- An io_uring, thread-per-core transport is a later, Linux-only experiment. Replacing only the socket layer under Kestrel's HTTP/2 does not help; the gain must come from owning the HTTP/2 layer.

## Prior art

grpc-dotnet gives methods `IAsyncStreamReader<T>`, `IServerStreamWriter<T>` and `ServerCallContext`. GRPC.NET keeps a similar method view but puts a transport-agnostic core underneath, removes the per-call context, serialization contexts, deadline manager and `Task`, and does not flush per message. pajamax (Rust) shows the built-in host's shape: one thread per connection, handlers inline, one flush per pass. grpc-go, h2o and nghttp2 show the single writer per connection that drains all ready streams into one write. Garnet shows inline processing and one send per receive in .NET.

## Questions closed by this design

| Question | Decision |
| --- | --- |
| 1. Metadata representation | `MetadataView` for synchronous methods, `MetadataHandle` for async ones, with string and byte lookups and lazy `-bin` decoding |
| 2. Deadline enforcement | ticks checked by the core around synchronous methods; a shared timer wheel for async ones; a cancellation source only when a method asks for a token |
| 3. Compression | not in milestone 1; other `grpc-encoding` values are answered with `UNIMPLEMENTED` |
| 4. Pre-split messages | answered by the span primitive: an in-process host passes each message as a span |
| 5. Backpressure | asynchronous, per stream, with DATA segmented to the available window |
| 6. Status mapping | the table above; rich status details deferred |
| 7. Interceptors | deferred; any future hook must not touch the synchronous fast path |
| 8. Request body type for `Process` | a span primitive plus a sequence overload that only branches |
| 9. Message ownership | owned by default; borrowed views only in the synchronous fast profile with the generated codec |

## Still open

- Exact public names and the shape of the method view for streaming methods.
- The flush thresholds, the per-stream queue limit and the shipped `InlinePolicy` default, all decided by milestone 1's measurements.
- Whether per-thread scratch retention is capped per buffer, as JSON-RPC.NET's 64 KiB, or by a process-wide budget.
