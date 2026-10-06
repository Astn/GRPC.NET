# GRPC.NET

GRPC.NET is planned as a gRPC server library for .NET, ported from [JSON-RPC.NET](https://github.com/Astn/JSON-RPC.NET). It keeps that library's programming model and speaks the [gRPC protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) on the wire. It aims for minimal dependencies and higher throughput than JSON-RPC.NET.

**Status:** nothing is implemented yet. The transport approach is decided; the other design questions below are still open.

## Goals

- **The JSON-RPC.NET model.**
  - Methods are plain members of a class, marked with an attribute.
  - Synchronous, `Task` and `ValueTask` methods are supported, with cooperative cancellation.
- **The gRPC protocol.** Standard gRPC clients in any language can call a GRPC.NET service.
- **Minimal dependencies.** Each package dependency has to justify itself, as JSON-RPC.NET's built-in serializer replaced its JSON library dependency.
- **Faster than JSON-RPC.NET.** Comparisons use the same calls on the same host, and their raw data is published.

## Transport

These decisions were made on 2026-10-05:

- **A transport-agnostic core.** As in JSON-RPC.NET, the core takes a call's method path, metadata and messages, and writes the reply's messages and status to a writer the caller provides. It has no dependency on HTTP or ASP.NET Core. A host owns framing, connections and limits, and an application can embed the core in a transport of its own.
- **Kestrel hosting out of the box.** A host package maps services into ASP.NET Core and serves the standard gRPC protocol over HTTP/2 and HTTP/3, so standard gRPC clients can call it.
- **A built-in hosting mode only if measured.** A server mode of GRPC.NET's own, such as a purpose-built HTTP/2 path, is added only if benchmarks show a large win over the Kestrel host.

## Baselines

These figures come from JSON-RPC.NET's comparison run on a 32-vCPU host, with five small calls and 256 requests in flight per connection. Its [README](https://github.com/Astn/JSON-RPC.NET#versus-streamjsonrpc-and-grpc) describes the method.

| Library and path | Calls per second |
| --- | ---: |
| JSON-RPC.NET over Kestrel TCP, raw documents, 32 connections | 16.4 M to 16.8 M |
| gRPC for .NET 2.84, unary calls over HTTP/2, 32 channels | 158 k to 166 k |
| gRPC for .NET 2.84, one bidirectional stream per channel, 32 channels | 703 k to 733 k |

The gRPC rows include the .NET client, which ran on the same cores as the server.

## Open design questions

1. **Contract.** Services could be defined in `.proto` files with generated code, in C# first with the schema derived from attributes, or both.
2. **The core's call interface.** It must cover streaming as well as unary calls, so the core accepts and produces message sequences, not one request and one reply. It must also say how a host passes deadlines, cancellation and metadata in, and how trailers come back. [CALL-INTERFACE.md](CALL-INTERFACE.md) is the current draft.
3. **Serialization.** Messages could be encoded with Google.Protobuf or with a built-in Protocol Buffers codec, as jsmn is built in for JSON-RPC.NET. [RESEARCH-MEMORY.md](RESEARCH-MEMORY.md) recommends a codec seam in the core, a Google.Protobuf adapter first, and a built-in codec only if benchmarks justify it.
4. **Call types.** These are unary, server streaming, client streaming and bidirectional streaming. The first release needs a subset of them.
5. **gRPC features.** These include deadlines, metadata, compression, rich status details, health checking and reflection, each to be supported or deferred.
6. **Interoperability.** Conformance could be proven against gRPC's interop test suite and against standard clients.
7. **Benchmarks.** "Faster than JSON-RPC.NET" needs a definition. Unary gRPC carries HTTP/2 headers and trailers on every call, which the raw TCP path does not. Unary calls and streams may therefore need separate targets, and the throughput of gRPC for .NET is a second baseline. The same benchmarks decide whether a built-in hosting mode earns its place, measuring the core alone, the core behind Kestrel, and any candidate built-in host. [RESEARCH-MEMORY.md](RESEARCH-MEMORY.md#questions-this-adds-to-the-design-review) question 5 describes the hand-off measurement that should come before any built-in host work.
8. **Targets.** The target frameworks are open, as is whether Native AOT and trimming are supported from the start.

## License

MIT. See [LICENSE](LICENSE).
