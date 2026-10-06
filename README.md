# GRPC.NET

GRPC.NET is planned as a gRPC server library for .NET, ported from [JSON-RPC.NET](https://github.com/Astn/JSON-RPC.NET). It keeps that library's programming model and speaks the [gRPC protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) on the wire. It exists for one reason: to serve gRPC with significantly higher throughput per server CPU than grpc-dotnet, the library .NET users already have.

**Status:** nothing is implemented yet. The transport approach (2026-10-05) and the call interface (2026-10-06) are decided. Milestone 1 is a measurement experiment that proves or ends the project.

## Goals

- **Significant throughput gains over grpc-dotnet.** The targets and the stop floor are under [Performance targets](#performance-targets). A design element that costs throughput needs a compensating reason.
- **The JSON-RPC.NET model.**
  - Methods are lambdas or plain members of a class marked with an attribute.
  - Every binding is checked against the `.proto` contract: wrong types or call kinds fail to compile, and anything left over fails at startup.
  - Synchronous, `Task` and `ValueTask` methods are supported, with cooperative cancellation.
- **The gRPC protocol.** Standard gRPC clients in any language can call a GRPC.NET service.
- **Minimal dependencies.** Each package dependency has to justify itself, as JSON-RPC.NET's built-in serializer replaced its JSON library dependency.

## Transport

These decisions were made on 2026-10-05 and refined on 2026-10-06:

- **A transport-agnostic core.** As in JSON-RPC.NET, the core takes a call's method, metadata and messages, and writes the reply's messages and status to a writer the caller provides. It has no dependency on HTTP or ASP.NET Core. A host owns framing, connections, flushing and limits, and an application can embed the core in a transport of its own.
- **Kestrel hosting out of the box.** A host package maps services into ASP.NET Core and serves the standard gRPC protocol over HTTP/2 and HTTP/3, so standard gRPC clients can call it. It is the interoperability deliverable.
- **A built-in HTTP/2 host, planned from the start and built third.** Kestrel dispatches every HTTP/2 stream to the thread pool, copies each payload twice and hands each response through a channel. A Kestrel-hosted library cannot remove those costs. The design review therefore expects the throughput premise to be decided by a built-in host that runs synchronous methods on the receive thread and writes many responses in one send. It ships only if it passes the targets below and full conformance testing.

[CALL-INTERFACE.md](CALL-INTERFACE.md) describes the interface between hosts, the core and methods.

## Performance targets

Every gate is measured at 32 connections with 256 calls in flight per connection, against a fresh grpc-dotnet baseline on the same host, with an out-of-process load generator. Each cell reports both calls per second and server CPU per call, from a true latency histogram, with responses validated by a standard client.

| Gate | Unary calls per server CPU | Bidirectional-stream messages per server CPU |
| --- | --- | --- |
| **Success**, best conformant host | at least 3× grpc-dotnet | at least 2× grpc-dotnet |
| **Stop floor**, best conformant host | below 2× ends the project, or the owner re-scopes it | below 1.5× ends the project, or the owner re-scopes it |
| **Kestrel host** | within 10% of the Kestrel floor, and never slower than grpc-dotnet | at least 1.5× grpc-dotnet |

- **Throughput must hold up end to end.** Calls per second must actually scale, and p99 latency must not regress at equal offered load.
- **The Kestrel floor** is a bare ASP.NET Core endpoint that reads a gRPC request and writes a fixed gRPC response with trailers. It measures how much of a call Kestrel itself costs, so it bounds what any Kestrel-hosted library can gain.
- **The streaming comparison** runs against grpc-dotnet as a user would write it: the client batches its writes and the server does not flush per reply.
- **One call in flight** is bounded by round trips for every library, so it is reported as CPU per call and latency. The targets are 1.5× for unary and 1.3× for streams, and these figures are never the headline.
- **Expectation, recorded so that the data can refute it:** the Kestrel host alone will not reach 2× on unary calls. If the Kestrel floor itself turns out at 2× or more grpc-dotnet per CPU, the Kestrel host gets its own success target.
- **Context, not targets.** Purpose-built gRPC servers in [HttpArena](https://github.com/MDA2AV/HttpArena) serve a small unary call at 2.7× to 2.9× grpc-dotnet's throughput on 25% to 50% of its CPU, without being CPU-saturated. These figures show the opportunity; they are not gates.

JSON-RPC.NET is no longer a target. Its fastest path pipelines header-free documents over TCP, while every unary gRPC call carries HTTP/2 headers and trailers. Reports keep two rows of context instead:

- GRPC.NET unary calls against JSON-RPC.NET's one-POST-per-call HTTP row;
- GRPC.NET stream messages against JSON-RPC.NET's pipelined TCP row.

## Baselines

The baselines this README previously quoted (gRPC for .NET at 158 k to 166 k unary calls per second on 32 vCPUs) are withdrawn. In that run, from JSON-RPC.NET's September comparison, the .NET gRPC client ran on the same cores as the server, and its own sweep peaked elsewhere. The figures measured the client and the round trips, not the server.

New baselines come from experiment E1 below and will be published here with their raw data.

## Milestone 1

Milestone 1 builds the smallest thing that can prove or kill the premise. The steps, in order:

1. **The core**, with the Google.Protobuf codec behind a struct-generic seam, unary and bidirectional calls, deadlines and status mapping. It includes the generator for typed method objects and service binders, together with the startup checks against the contract ([Service registration](CALL-INTERFACE.md#service-registration)).
2. **A benchmark harness for the core alone.**
3. **The Kestrel host.**
4. **A raw-framing diagnostic host.**
5. **The built-in h2c host.**

| Run | Measures |
| --- | --- |
| E0 | The core alone: nanoseconds and bytes per call for scalar messages of 8, 32, 512 and 4,096 bytes, a string-heavy message, a nested message and a bytes field; and handler dispatch compared four ways: a delegate, a delegate devirtualized by PGO, a direct call, and a generated `switch` |
| E1 | grpc-dotnet on Kestrel (the baseline), and the Kestrel floor |
| E2 | GRPC.NET on Kestrel |
| E3 | The same core behind a raw-framing host on Kestrel's socket transport, with no HTTP/2: a diagnostic of what bypassing Kestrel's HTTP/2 path could be worth |
| E4 | Variants that isolate the thread hop: Kestrel with inline scheduling, E3 with inline socket completions, and E3 with forced dispatch |
| E5 | Stalls and fairness: 1% of calls hitting a 1 ms synchronous method, and a stream whose client stops reading beside one that does not |
| E6 | The built-in h2c host: the full matrix plus conformance |

- **E3 decides nothing by itself.** If E3 is below 2× grpc-dotnet per server CPU in both socket modes, the decision to build the HTTP/2 host goes to the owner with the numbers.
- **The measurement protocol** applies to every cell:
  - one disclosed x64 host with 32 or more cores, pinned server cores, and the load generator on separate cores or a second machine;
  - a fixed .NET version, Release builds and Server GC;
  - 1 and 32 connections, with 1 and 256 calls in flight per connection;
  - unary calls, and one established bidirectional stream per connection;
  - at least five warmed runs per cell, with raw samples and exact commands published.
- **Each cell reports:**
  - calls per second;
  - server CPU microseconds per call;
  - allocated bytes per call;
  - GC counts;
  - p50 and p99 from a histogram;
  - socket writes per call.
- **Saturation.** A cell counts only if the server cores are at least 90% busy and doubling the offered load does not change the result. Otherwise it is marked client-bound.
- **Load generators.**
  - `h2load` for throughput.
  - A standard gRPC client that validates every response, because `h2load` counts HTTP status only and reports its maximum latency as "p99".
- **Public comparability.**
  - One run in the style of [grpc_bench](https://github.com/LesnyRumcajs/grpc_bench) with 1 and 4 server cores.
  - An entry in HttpArena, so a third party measures the same calls on the same machine.

**Gates on every change:**

- **Allocations.** Allocated bytes per call for the core may not exceed the recorded value by more than 10%. The core itself allocates nothing on the synchronous path; the codec's own message allocations are reported separately.
- **Interop.** gRPC's interop test cases for the features milestone 1 implements, run against the C++, Go and Java clients.
- **CPU.** CPU per call on a dedicated machine may not regress by more than 10% over three runs. Throughput is not gated on shared CI.

**Release gates:**

- **Built-in host.** It may not ship before `h2spec --strict` passes in full, together with malformed-frame tests and protections against Rapid Reset and CONTINUATION floods.
- **Kestrel host.** It needs an HTTP/3 smoke and interop run.

**Not in milestone 1:**

- compression beyond identity;
- rich status details;
- reflection and health services;
- interceptors;
- a client library;
- Native AOT;
- a generated protobuf codec and borrowed message views;
- HTTP/3 testing;
- an io_uring transport.

## Design questions

| Question | Status |
| --- | --- |
| 1. Contract | Decided: messages and services are defined in `.proto`, the only wire contract. Implementations are C# lambdas or attribute-marked classes, bound to typed method objects generated from the `.proto` and checked against it ([Service registration](CALL-INTERFACE.md#service-registration)). |
| 2. The core's call interface | Decided: [CALL-INTERFACE.md](CALL-INTERFACE.md). |
| 3. Serialization | Decided for milestone 1: Google.Protobuf behind a struct-generic codec seam. A codec generated from `.proto` follows only if profiling shows the codec takes at least 15% of server CPU per call, or its allocations cost at least 3% of throughput. |
| 4. Call types | Milestone 1 covers unary and bidirectional streaming; client and server streaming use the same reader and writer. |
| 5. gRPC features | Deadlines, metadata and status are in milestone 1. Compression, rich status details, health checking, reflection and interceptors are deferred. |
| 6. Interoperability | Decided: gRPC's interop suite against standard clients on every change; `h2spec --strict` for the built-in host. |
| 7. Benchmarks | Decided: [Performance targets](#performance-targets) and [Milestone 1](#milestone-1). |
| 8. Targets | Open: target frameworks, and when Native AOT and trimming are supported. |

[RESEARCH-MEMORY.md](RESEARCH-MEMORY.md) holds the research behind these decisions.

## License

MIT. See [LICENSE](LICENSE).
