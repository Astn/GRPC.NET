# Call interface: design draft

**Status:** draft of 2026-10-05. It has not yet been through the design review that settles the README's open questions. The signatures below illustrate the shape; they are not an API.

This document describes how a gRPC call moves between a host, the GRPC.NET core and a service method. It builds on the transport decisions in the [README](README.md#transport): the core is transport-agnostic, Kestrel hosting over HTTP/2 and HTTP/3 ships out of the box, and a built-in hosting mode is added only if benchmarks justify it.

## What one gRPC call carries

The [gRPC over HTTP/2 specification](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) allows exactly this sequence on one call:

1. **Request headers**, first: the method path (`/package.Service/Method`), the deadline in `grpc-timeout`, `content-type`, `grpc-encoding`, and any custom metadata.
2. **Request messages**: zero or more. Each message carries gRPC's own 5-byte prefix, a 1-byte compressed flag followed by a 4-byte big-endian length.
3. **Response headers**: optional initial metadata, which must precede the first response message.
4. **Response messages**: zero or more, with the same prefix.
5. **Trailers**, last: `grpc-status`, `grpc-message` and trailing metadata.

A call that fails before writing any message may send a **trailers-only** response: one header block that carries the status and ends the stream.

## Two lanes

The call splits into two lanes that behave differently.

- **The data lane** is two ordered byte streams, one per direction. Messages flow continuously, and their framing (the 5-byte prefix) belongs to gRPC, not to HTTP/2.
- **The control lane** carries metadata and signals.
  - **Metadata appears only at fixed points:** request headers at the start, response headers before the first message, trailers at the end.
  - **Signals:** cancellation (the client resets the stream or the connection drops) and deadline expiry.

Metadata is therefore modelled as a call context with a few boundary events, not as a channel. A general metadata channel would allow interleavings of metadata and messages that HTTP/2 cannot carry.

## Layers

| Layer | Owns | Does not own |
| --- | --- | --- |
| **Host** (the Kestrel package, a built-in host, or an application's own transport) | Connections, HTTP/2 and HTTP/3 framing, header encoding, flow control, connection limits, and turning `grpc-timeout` and stream resets into a deadline and a cancellation token | Message framing, method lookup, serialization |
| **Core** | Method lookup by path, the 5-byte message framing, message size limits, serialization, invocation, mapping exceptions to status codes, and enforcing the order of headers, messages and trailers | Sockets, HTTP, ASP.NET Core |
| **Method view** | A typed reader and writer of messages, and a call context | Bytes and framing |

The host is a dumb pipe. It hands the core the request headers and the request body bytes, then carries the response bytes, a header event and a trailer event back out. This mirrors JSON-RPC.NET, where the processor finds document boundaries in raw bytes and the host only moves them.

## Sketch

```csharp
// Everything the host knows when a call starts.
public readonly struct CallHead
{
    public ReadOnlyMemory<byte> Path { get; }          // "/package.Service/Method" as UTF-8
    public IMetadata RequestMetadata { get; }          // a view over the host's decoded headers
    public long DeadlineTicks { get; }                 // from grpc-timeout; 0 when absent
    public CancellationToken Cancellation { get; }     // stream reset, connection loss or deadline
}

// The boundary events a host must carry back out, in this order.
public interface ICallEvents
{
    ValueTask WriteHeadersAsync(IMetadata responseMetadata);        // before the first message; implicit if never called
    void Complete(GrpcStatus status, IMetadata trailingMetadata);   // last; trailers-only when no message was written
}

public sealed class GrpcProcessor
{
    // Lets the host choose a path before reading the body.
    public MethodKind Resolve(ReadOnlySpan<byte> path);

    // Unary fast path: one complete request message in, one response written, status returned.
    // Synchronous methods complete without allocating per call, as in JSON-RPC.NET.
    public GrpcStatus Process(in CallHead head, ReadOnlySpan<byte> requestBody, IBufferWriter<byte> responseBody);

    // General path for every call type: byte pipes for data, events for the boundaries.
    public ValueTask ProcessAsync(CallHead head, PipeReader requestBody, PipeWriter responseBody, ICallEvents events);
}
```

The method view sits above the core. A unary method is a plain method, such as `Reply Add(Request r)`, or its `Task` and `ValueTask` forms. A streaming method receives a typed message reader, a typed message writer, or both. It also receives a call context exposing:

- the request metadata, the deadline and the cancellation token;
- a way to send response headers early;
- the trailing metadata.

## Call types

| Call type | Request messages | Response messages | Core entry point |
| --- | --- | --- | --- |
| Unary | 1 | 1 | `Process` when the host has the whole request body; otherwise `ProcessAsync` |
| Server streaming | 1 | 0 or more | `ProcessAsync` |
| Client streaming | 0 or more | 1 | `ProcessAsync` |
| Bidirectional streaming | 0 or more | 0 or more | `ProcessAsync` |

The wire does not say which kind a call is; the method's definition does. So the host first asks the core with `Resolve(path)`. For a unary method whose request body is complete and small, the host can buffer it and take the synchronous fast path. In HTTP/2 the body is complete when the request stream has ended.

## Performance notes

- **Unary first.** Most gRPC traffic is unary, so the fast path must avoid async state machines, queues and per-call allocation.
- **No `System.Threading.Channels` on the hot path.** A channel costs a queue operation and synchronization per message. Byte pipes with framing parsed in place are cheaper, and the typed reader and writer are a thin layer over the pipe.
- **Batched writes.** Response messages for many calls should leave in as few socket writes as possible. JSON-RPC.NET's TCP handler flushes once per read group; the HTTP/2 hosts should do the same across streams.
- **Cheap errors.** A failure before the first response message uses a trailers-only response.
- **Metadata as views.** Request metadata is read in place from the host's decoded headers, not copied into a dictionary per call.

## The Kestrel host

| gRPC concept | ASP.NET Core |
| --- | --- |
| Method path, request metadata | `HttpRequest.Path`, `HttpRequest.Headers` |
| Deadline | the `grpc-timeout` request header, parsed by the host |
| Cancellation | `HttpContext.RequestAborted`, linked with the deadline |
| Request body | `HttpRequest.BodyReader` |
| Response body | `HttpResponse.BodyWriter` |
| Response headers | `HttpResponse.Headers`, then `HttpResponse.StartAsync` |
| Trailers | `HttpResponse.AppendTrailer`, available when `SupportsTrailers()` is true |

## Prior art

grpc-dotnet gives methods the same split: `IAsyncStreamReader<T>` and `IServerStreamWriter<T>` for messages, and `ServerCallContext` for request headers, response trailers, the deadline, cancellation and `WriteResponseHeadersAsync`. Its server is built on ASP.NET Core. GRPC.NET keeps a similar method view but puts a transport-agnostic core underneath it.

## Open questions

1. **Metadata representation.** How the core reads request metadata without copying it, and how binary (`-bin`) headers are exposed.
2. **Deadline enforcement.** JSON-RPC.NET leaves deadlines to the host. gRPC carries the deadline in the protocol, so the core may need to enforce it, or at least link it into the cancellation token.
3. **Compression.** Whether the first release supports `grpc-encoding`, and where decompression happens.
4. **Pre-split messages.** Whether a host such as an in-process transport can hand the core messages that are already split, and skip the 5-byte framing.
5. **Backpressure.** How a slow reader on one stream limits a fast writer without stalling other streams on the same connection.
6. **Status mapping.** How exceptions map to gRPC status codes and rich status details.
7. **Interceptors.** Whether cross-cutting hooks exist, and where they sit relative to the fast path.
