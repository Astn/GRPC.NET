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
    public HostCallState State { get; }        // host-owned handle: metadata, cancellation state, stream identity
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

`Process` guarantees what the hosts rely on: it is re-entrant, allocates nothing beyond what the codec allocates for the messages, never flushes, never awaits, and writes into whatever `IBufferWriter<byte>` the host passes. On the built-in host that writer is the connection's send buffer itself.

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
public sealed class MessageReader<T>
{
    public ValueTask<bool> MoveNextAsync();
    public ref readonly T Current { get; }     // valid until the next MoveNextAsync
}

public sealed class MessageWriter<T>
{
    public ValueTask WriteAsync(in T message);  // sizes, encodes and advances; never flushes
}
```

- One reader and one writer per stream, pooled per connection along with the stream. Each implements `IValueTaskSource`, so a call that completes synchronously allocates nothing. At deep pipelining that is the common case, because the next message is usually already buffered.
- No `System.Threading.Channels` and no `Channel<T>` per call.
- The reader advances the pipe past a message only on the next `MoveNextAsync`. Owned messages survive an `await`.
- Borrowed views of streamed messages come with the generated codec, in a synchronous `OnMessage(in TView)` callback valid until it returns.

**Backpressure** never blocks a thread.

- **Kestrel** already applies stream and connection windows asynchronously in its output producer; nothing is added.
- **The built-in host's writer** segments DATA to the smaller of the stream window, the connection window and the maximum frame size. It releases credit as frames leave, and the stream awaits more credit while other streams advance. A message never has to fit the current window, which would deadlock on any message larger than the 65,535-byte initial window.
- Each stream has a bounded queue of pending bytes (64 KiB by default, to be measured). Beyond it, `WriteAsync` stays pending; the connection's send buffer is never held.
- The writer skips window-stalled streams and keeps order within each stream.
- On the receive side, a stream's `WINDOW_UPDATE` is sent only after its reader has consumed the bytes, so a slow method throttles only its own stream.

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
- **Invokers.** Each method binds a closed `TCodec : struct` and `TInvoker : struct` pair once, into the method table: no reflection, no delegate allocation and no generic discovery per call, as in Tsavorite's struct callbacks.
- **Contracts.** Services are declared C#-first, as in JSON-RPC.NET: `[GrpcService("package.Service")]` classes with `[GrpcMethod]` methods. The method table is built at startup. Messages are defined in `.proto`, the only wire contract, which foreign clients need anyway. A C#-first message schema is not planned: it would remove no hot-path work and double the conformance work.
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
