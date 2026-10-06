# GRPC.NET

GRPC.NET is planned as a gRPC server library for .NET, ported from [JSON-RPC.NET](https://github.com/Astn/JSON-RPC.NET). It keeps that library's programming model and speaks the [gRPC protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) on the wire. It aims for minimal dependencies and higher throughput than JSON-RPC.NET.

**Status:** nothing is implemented yet. The design questions below are still open.

## Goals

- **The JSON-RPC.NET model.**
  - Methods are plain members of a class, marked with an attribute.
  - The hosting options mirror JSON-RPC.NET's: ASP.NET Core, raw Kestrel connections, and a processor for transports you own.
  - Synchronous, `Task` and `ValueTask` methods are supported, with cooperative cancellation.
- **The gRPC protocol.** Standard gRPC clients in any language can call a GRPC.NET service.
- **Minimal dependencies.** Each package dependency has to justify itself, as JSON-RPC.NET's built-in serializer replaced its JSON library dependency.
- **Faster than JSON-RPC.NET.** Comparisons use the same calls on the same host, and their raw data is published.

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
2. **Transport.** HTTP/2 could come from Kestrel, from a minimal built-in HTTP/2 implementation, or from both. HTTP/3 is a further option.
3. **Serialization.** Messages could be encoded with Google.Protobuf or with a built-in Protocol Buffers codec, as jsmn is built in for JSON-RPC.NET.
4. **Call types.** These are unary, server streaming, client streaming and bidirectional streaming. The first release needs a subset of them.
5. **gRPC features.** These include deadlines, metadata, compression, rich status details, health checking and reflection, each to be supported or deferred.
6. **Interoperability.** Conformance could be proven against gRPC's interop test suite and against standard clients.
7. **Benchmarks.** "Faster than JSON-RPC.NET" needs a definition. Unary gRPC carries HTTP/2 headers and trailers on every call, which the raw TCP path does not. Unary calls and streams may therefore need separate targets, and the throughput of gRPC for .NET is a second baseline.
8. **Targets.** The target frameworks are open, as is whether Native AOT and trimming are supported from the start.

## License

MIT. See [LICENSE](LICENSE).
