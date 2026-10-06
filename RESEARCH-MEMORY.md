# Research: memory, buffers and zero-allocation marshalling

**Status:** research of 2026-10-05, extended 2026-10-06 with FASTER, Tsavorite and Garnet, and with published gRPC server throughput (finding 8). The second design review of 2026-10-06 decided most of what follows. [What the design review decided](#what-the-design-review-decided) records which recommendations were accepted, changed or left as hypotheses, and the experiment that settles each. The decisions themselves are in [README.md](README.md#design-questions) and [CALL-INTERFACE.md](CALL-INTERFACE.md).

This document summarizes four sets of notes. Each note pins every claim to a source file and line at a recorded commit, a documentation page or a paper, and marks its own inferences.

| Note | Covers |
| --- | --- |
| [research/memory/flatsharp.md](research/memory/flatsharp.md) | FlatSharp (James Courtney): deserialization modes, buffer abstractions, code generation, pooling, gRPC integration, benchmarks |
| [research/memory/protobuf-grpc-dotnet.md](research/memory/protobuf-grpc-dotnet.md) | Google.Protobuf and protobuf-net span APIs, grpc-dotnet's serialization seams and per-call costs, Kestrel's HTTP/2 memory model, string decoding |
| [research/memory/faster-tsavorite.md](research/memory/faster-tsavorite.md) | FASTER KV and FasterLog, their successors Tsavorite and TsavoriteLog, and Garnet's own TCP host: epoch protection, the hybrid log, latch-free tail reservation and group commit, struct-generic callbacks, in-place parsing and batched sends |
| [research/memory/arenas-zero-alloc.md](research/memory/arenas-zero-alloc.md) | .NET building blocks (spans, `Memory<T>`, pools, the pinned heap, native memory, ref structs), arena patterns in .NET projects, arenas elsewhere, other zero-copy serializers, the research literature |

## Findings

### 1. .NET has no arena for managed objects, and none is coming

- The runtime request for custom allocators ([dotnet/runtime#5633](https://github.com/dotnet/runtime/issues/5633)) has been open since 2016.
- The request for arenas in C# protobuf ([protocolbuffers/protobuf#3530](https://github.com/protocolbuffers/protobuf/issues/3530)) was closed because it would need a rewrite.
- Google.Protobuf has no whole-message `Clear` for reuse ([#18782](https://github.com/protocolbuffers/protobuf/issues/18782), closed as not planned).

What .NET projects do instead:

- **Kestrel** pools 4 KB blocks on the pinned object heap and evicts idle ones.
- **Garnet** gives each session a scratch area that must be rewound in order.
- **FASTER** reuses pages only after an epoch has passed.
- **Orleans** reference-counts pages and gives each slice a version token, so access after return throws.
- **MemoryPack** deserializes into an existing object.

JSON-RPC.NET already has the pattern that matters most: per-thread scratch for the synchronous path, and leases that travel with an async continuation, capped at 64 KiB.

### 2. Only a ref struct gives a lifetime the compiler checks

- A `Span<T>` or ref struct cannot outlive a synchronous call or cross an `await`. The compiler enforces this.
- `Memory<T>` lifetimes rest on convention, set out in the usage guidelines' rules 3 to 8.
- `ArrayPool` detects neither a double return nor use after return, and rented arrays keep the previous renter's bytes.
- Kestrel's pool is shared by every connection on an I/O queue. A use-after-return bug would corrupt an unrelated call rather than fail.

Rust's borrow checker gives the same guarantee for arenas in general; .NET gives it only inside one synchronous call. Go shelved its arena experiment because arena parameters spread through every API.

### 3. Most messages are small, so fixed per-call costs dominate

At Google, protobuf work takes 9.6% of fleet cycles, and 93% of messages are 512 bytes or less ([ProtoAcc, MICRO 2021](https://sagark.org/assets/pubs/protoacc-micro2021-preprint.pdf)). Cornflakes ([SOSP 2023](https://amyousterhout.com/papers/cornflakes_sosp23.pdf)) found that avoiding a copy pays only for fields of about 512 bytes and up. Below that, tracking a reference costs more than copying.

So the costs to remove are the fixed ones paid on every call:

- the message object;
- a `byte[]`;
- a `Task`;
- a metadata dictionary.

Avoiding byte copies matters only for large fields.

### 4. FlatSharp: what to copy and what to avoid

**Copy:**

- **Code specialized per buffer type.** Hot paths are generic over struct buffer types, so the JIT specializes them with no interface dispatch.
- **Safe code by default.** Reads go through bounds-checked span slices, with depth and size limits. Unsafe code is opt-in for each schema element.
- **Reused per-thread state.** Serialization context is kept per thread and reset for each call rather than allocated.
- **Code generated at build time.** There is no reflection, and CI runs the full test suite under NativeAOT.
- **Re-sending without re-encoding.** A message that was received can be written out again as a straight memory copy.

**Avoid:**

- **Pooling message objects.** FlatSharp tried it, measured about half the speed of plain allocation, found use-after-return hazards, and removed it in 7.8.
- **Lazy objects that keep the input buffer.** Reusing the buffer while such an object is alive is undefined behaviour. Only documentation guards against it.
- **Reserving worst-case space on write.** `GetMaxSize` counts three bytes per string character, and the reservation goes to the shared pipe.
- **Copying every received message.** FlatSharp's gRPC marshaller calls `PayloadAsNewBuffer`: one copy and one array per message, which is the price of making Lazy objects safe.

Lazy mode is not free of allocation either: every access to a string or child object allocates. No gRPC throughput figures for FlatSharp exist.

### 5. Google.Protobuf reads and writes in place but allocates the whole message graph

`ParseFrom(ReadOnlySequence<byte>)` and `WriteTo(IBufferWriter<byte>)` are public, and grpc-dotnet's generated marshallers use them. Parsing still allocates:

- every message and nested message;
- every `RepeatedField` and its backing array, even when the field is empty;
- one `string` per string field;
- one copied `byte[]` per `bytes` field.

Writing has two costs as well:

- nested messages and strings are measured again on every write, because no size is cached;
- a string that straddles a 4 KB output block goes through a new array.

The parse and write contexts are internal, and a request to make them public was closed as not planned ([#8036](https://github.com/protocolbuffers/protobuf/issues/8036)).

### 6. What Kestrel costs, and what grpc-dotnet adds

Any library hosted on Kestrel pays these costs for each call:

- **Two copies of the payload.** Request bytes are copied from the socket pipe into a per-stream pipe. Response bytes are copied from a per-stream pipe into the connection pipe.
- **A thread-pool hop.** Each stream is dispatched to the thread pool. This cannot be configured away. The inline path in `Http2Connection` is used only in tests, and its comment says app code must not block the connection loop ([Http2Connection.cs#L1380-L1389](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L1380-L1389)). The socket transport's `UnsafePreferInlineScheduling` option changes only the transport's pipe schedulers, not this dispatch.
- **A channel hand-off.** Each response goes through a channel to the connection's writer loop.
- **Header work.** HPACK decoding, plus a new string for every custom metadata value and every `grpc-timeout` value that changes.

grpc-dotnet adds further costs that GRPC.NET's design does not need:

- a call context object;
- serialization and deserialization context objects;
- a deadline manager when `grpc-timeout` is present;
- `Metadata` collections, if read;
- the service instance, unless it is registered;
- the `Task<TResponse>`;
- an intermediate `ArrayBufferWriter` when the payload length is unknown.

### 7. FASTER, Tsavorite and Garnet: epochs, batched sends, and no thread hand-offs

- **Epochs are cheap but tied to a thread.** Tsavorite's epoch protection gives each thread a 64-byte slot. Entering and leaving cost about one compare-and-swap, measured at 35M pairs per second per thread. Memory is reclaimed only after every thread has moved past the epoch in which it was retired. Because the slot belongs to an OS thread, an epoch suits short synchronous regions and rarely written shared state, such as a method table. It cannot cover a buffer that lives across an `await`, so leases and reference counts stay the right tools there.
- **The log reserves space without a lock.** Writers reserve space with one fetch-and-add on the tail and copy their bytes in parallel. Per-thread in-flight marks give the fully written prefix, and that prefix is committed in groups, with one completion shared by all waiters. The single tail becomes the bottleneck as writer threads grow.
- **Callbacks are struct type parameters.** Tsavorite passes its callbacks as struct type parameters with `ref` arguments, so the JIT specializes and inlines them. Class implementations get the same effect through a struct wrapper. CI fails a build when allocated bytes exceed the expected value by more than 10%.
- **Garnet's TCP host skips ASP.NET Core.** It works like this:
  - It receives into one contiguous, growable, pinned 128 KiB buffer per connection.
  - It parses requests in place on the receive thread.
  - It writes every response for one receive into one pooled pinned buffer, then sends it once.
  - CI expects 0 bytes allocated through the network path, measured by driving an in-process handler with no real socket.
- **Keeping work on the receive thread paid off by an order of magnitude.** Garnet's design study ([Garnet, PVLDB 2025, §8.5](https://www.vldb.org/pvldb/vol19/p224-chandramouli.pdf)) compared five designs on the same workload:

  | Design | Throughput |
  | --- | --- |
  | Work stays on the receive thread | 47 Mops/s |
  | Network threads hand requests to storage workers | 4.4 to 9 Mops/s |
  | Separate processes | 17.1 Mops/s |
  | One lock around the store | 1.3 Mops/s |

  The hand-offs measured were to storage workers, in batches of 1,024 commands. That is not the same thing as Kestrel's per-stream dispatch, so the size of Kestrel's hop cost is still to be measured.
- **Cautions.**
  - RESP, the protocol Garnet speaks, is pipelined and not multiplexed, so it is far simpler than HTTP/2.
  - Garnet blocks threads on TLS, on its send throttle and on accept backoff.
  - Every headline figure relies on deep client pipelining.
  - No source compares Garnet's host with Kestrel.
  - Running inline is safe for Garnet because it runs only its own short command handlers. A gRPC host that runs user methods inline lets one slow method stall every other stream on its connection. That is the reason Kestrel gives for always dispatching.

### 8. Published gRPC server throughput: grpc-dotnet is fast for a general-purpose server, and purpose-built hosts are much faster

- **grpc_bench** ([LesnyRumcajs/grpc_bench](https://github.com/LesnyRumcajs/grpc_bench), discussions 559, 441 and 547). grpc-dotnet ranks near the top of general-purpose servers per core, at about 85 k to 177 k unary calls per second per core. tonic and the C++ server are within 1.0× to 1.2× of it. Its multi-core rows are limited by the `ghz` client.
- **HttpArena** ([MDA2AV/HttpArena](https://github.com/MDA2AV/HttpArena)) runs a 9-byte unary `GetSum` call on one 64-core Threadripper, driven by `h2load` with 256 connections × 100 streams.
  - grpc-dotnet (.NET 10, Grpc.AspNetCore 2.71) did 2,491,232 calls per second on 56.2 CPUs, or 44 k per CPU.
  - Four purpose-built servers did 6.8 M to 7.3 M on 14 to 28 CPUs, so 2.7× to 2.9× the throughput on 25% to 50% of the CPU. They were not CPU-saturated, so their limiter is unidentified.
  - They also send more bytes per response than grpc-dotnet (63 to 133 B against 37 B), so they are not skipping parts of the response.
- **HttpArena cautions.**
  - Its Kestrel row is a GET with no body or trailers, so subtracting it from the gRPC row bounds nothing about grpc-dotnet's own layer.
  - The same `h2load` setup reached 15.7 M on GET, so 7 M is not the client's ceiling.
  - Its "p99" field holds `h2load`'s maximum.
  - Its throughput counts HTTP 2xx only; `grpc-status` is checked only before the run.
- **pajamax** ([WuBingzheng/pajamax](https://github.com/WuBingzheng/pajamax)) is a Rust server built only for gRPC. It runs one thread per connection, handles each call inline, and flushes once per read pass.
  - Against tonic, it uses 3× to 10× less CPU per request at 100 to 1,000 calls in flight per connection, and 1.3× to 2.9× less at one.
  - About 88% of its remaining CPU goes to send and receive system calls.
  - It skips parts of HTTP/2 conformance, so it shows the shape of a fast host, not a conformant target.
- **ioxide** ([MDA2AV/ioxide](https://github.com/MDA2AV/ioxide)) is a .NET runtime built on io_uring, with a thread per core, inline continuations and its own HTTP/2. GenHTTP on ioxide served HttpArena's h2c GET at 3.3× Kestrel's throughput per CPU. GenHTTP on Kestrel performs like Kestrel, so replacing only the socket layer under Kestrel's HTTP/2 does not help.
- **How fast HTTP/2 servers batch writes.** grpc-go, gRPC C++ (chttp2), h2o, Rust `h2` and nghttp2 all have one writer per connection. That writer owns HPACK and the flow-control windows; streams only enqueue. It drains everything ready into one write, aiming for 32 to 128 KiB.
- **Kestrel since .NET 7** has a single writer loop ([dotnet/aspnetcore#40925](https://github.com/dotnet/aspnetcore/pull/40925)). HPACK encoding still runs under a lock ([#41224](https://github.com/dotnet/aspnetcore/issues/41224), open), and the output channel is bounded.
- **The error path.** grpc-dotnet served 494 k calls per second on success and 116 k when the handler threw ([grpc/grpc-dotnet#2033](https://github.com/grpc/grpc-dotnet/issues/2033)).
- **Codec share.** At grpc-dotnet's speed, Google.Protobuf parsing is a few percent of a call. Its share grows on a fast host, on large fields and on streams. vtprotobuf's pooling made long streams 6.6× faster in Go.

## Recommendations for the design review

These are the notes' inferences, reconciled where they disagreed.

### R1. Accept the request body as a sequence and decode from a span

The first three notes disagreed on `Process`'s input:

- **FlatSharp note:** keep the contiguous span the draft has now.
- **Protobuf note:** take `in ReadOnlySequence<byte>`, because a span forces the host to copy any body that spans more than one 4 KB block.
- **Arenas note:** take a span, copied once into per-thread scratch when the body arrives in pieces.

**Reconciled recommendation:** `Process` takes `in ReadOnlySequence<byte>`, as JSON-RPC.NET's endpoint already does.

- When the body is a single segment (`IsSingleSegment`), the core decodes `FirstSpan` directly.
- Otherwise it copies the message once into per-thread scratch, bounded by the maximum message size, and decodes from that.

This keeps the copy out of every host and keeps the codec on contiguous spans, which FlatSharp's experience and protobuf's sequential format both favour. The core reads the 5-byte prefix in place, falling back to a stack copy when the prefix straddles segments.

A built-in host could follow Garnet instead: one contiguous, growable receive buffer per connection, so that every request arrives as a single segment and R1's copy path stays cold. That costs 128 KiB or more per connection, plus a shift of leftover bytes after each receive.

### R2. Measure the response, then write it

- **Why measure first.** A `PipeWriter` span cannot be written after `Advance`, so the core cannot reserve the prefix and fill it in afterwards.
- **The write path.** The core computes the exact payload size, writes the 5-byte prefix, then writes the message straight into the host's `IBufferWriter<byte>` with no intermediate buffer.
- **Size caching.** A built-in codec should cache sizes within one serialize, which Google.Protobuf does not do.
- **Exceptions on the async path.** When a method may throw after the response is partly written, stage the response in a pooled writer that can rewind, as JSON-RPC.NET does. That keeps the trailers-only error response clean.

### R3. Messages own their data by default; borrowing is opt-in and checked by the compiler

1. **Default.** A message handed to a user method is an ordinary object that owns its strings and bytes, the equivalent of FlatSharp's Greedy mode. User code may keep it indefinitely.
2. **Opt-in view.** A synchronous method may instead take a generated `ref struct` view over the request bytes. It has lazy accessors such as `ReadOnlySpan<byte> NameUtf8` and `string Name`. The compiler stops the view from escaping, which is the guarantee FlatSharp's Lazy mode lacks.
3. **Streaming.** A message read from a stream is valid until the next read. Borrowed fields must be copied out explicitly to be kept.
4. **Stream writes.** `WriteAsync(message)` serializes before it returns, so the caller may reuse the message at once.
5. **Large `bytes` fields.** These may be `ReadOnlyMemory<byte>` over leased pages, valid until the method's task completes. This is opt-in only, with version-checked pages in debug builds (the Orleans pattern) and a poisoning test pool (the Kestrel `DiagnosticMemoryPool` pattern).

### R4. Pool bytes, not message objects

- **What to pool.** Byte buffers come from pools: the host's blocks, per-thread scratch, and leases.
- **What not to pool.** The core does not pool message objects. FlatSharp measured that pooling was slower, and the hazards are real.
- **Overwrite deserialization.** Reusing one message object per stream, in the MemoryPack style, is a possible later opt-in, but only if measurements show it pays.
- **Memory the core does not own.** It creates no pinned or native memory of its own in the first release. Kestrel already owns and evicts its pinned blocks, and native memory turns use-after-free into undefined behaviour.
- **Scope.** This rule is for the core. A built-in socket host would almost certainly want pinned send and receive buffers, as Kestrel, FASTER and Garnet all use. Any `unsafe` code would stay inside that host, and the core would see only spans and `IBufferWriter<byte>`.

### R5. A codec seam in the core, a Google.Protobuf adapter first, and a built-in codec only if measured

All four notes support this staging for README question 3:

1. **The seam.** The core defines a codec interface: decode from a span, report the exact size, and encode into an `IBufferWriter<byte>`. Struct-generic implementations avoid interface dispatch.
2. **First codec.** A Google.Protobuf adapter, built on the public `ParseFrom`, `CalculateSize` and `WriteTo` APIs, gives `.proto` interop from day one.
3. **Built-in codec.** A generated protobuf codec is added only if benchmarks show the savings. It would use a source generator and C#-first contracts in the style of protobuf-net, and it could offer:
   - ref struct views;
   - UTF-8 string accessors;
   - struct messages;
   - cached sizes.

   It would need conformance work comparable to FlatSharp's: round-trip tests against Google.Protobuf, fuzzing, and the gRPC interop suite.

Tsavorite's struct-generic callbacks are a production precedent for this seam. They support generated `readonly struct` method invokers with `in` and `ref` parameters, bound once at registration. The cost is one JIT body per instantiation, which generated code keeps explicit for Native AOT.

The claim "no allocation per call" belongs to the built-in codec on scalar or view-typed messages. With class-typed messages, the message objects and their strings are the user's choice and are reported as such.

### R6. Strings

Three options, in order of preference:

- **Copy by default.** The default materializes a `string` per field.
- **UTF-8 views.** Views expose UTF-8 bytes, which must still be validated because protobuf requires valid UTF-8 in `string` fields.
- **A cache for repeated values.** For low-cardinality values such as tenant ids or enum-like strings, a cache keyed by the UTF-8 bytes could avoid the allocation itself. Kestrel's reuse of known-header values is the precedent.

No shipping `Utf8String` type exists. Tsavorite and Garnet never turn keys into `string`; they hash and compare the bytes.

### R7. Batch writes per pass; keep epochs to synchronous, shared state

- **Batching writes.** Write all the responses produced by one read pass before a single flush. Garnet and JSON-RPC.NET both do this, and it works behind Kestrel's `PipeWriter` too. Responses that complete outside a read pass, such as async methods and server streams, flush when the input runs dry or when a byte or time threshold is reached. All waiters for a flush share one completion.
- **A built-in host's send path.** A built-in host could go further. Streams would reserve DATA frames in a per-connection send buffer with one atomic add, following TsavoriteLog's design, and a single sender would send the written prefix. This would need several things:
  - header encoding serialized under a lock, or done without HPACK's dynamic table;
  - flow-control windows checked before reserving, so that a blocked stream never holds the tail;
  - a measurement of tail contention.
- **Inline execution in a built-in host.** The synchronous unary fast path (`Process`) is what would let a built-in host run calls on the receive thread, as Garnet does. Async methods need a hand-off anyway. To avoid one slow method stalling the connection, inline execution should be limited to methods that opt in or are marked short, and the cost of stalls should be measured with a deliberately slow method.
- **Epochs.** Use them, if anywhere, for rarely written shared state such as the method table. Never use them for buffers that cross an `await`.
- **What not to borrow.** Raw pointers as the general programming model, blocking waits on I/O threads, fixed process-wide limits, and zeroing every returned buffer.

## What the design review decided

The second design review (2026-10-06) treated R1 to R7 as hypotheses. Its outcome:

| Recommendation | Outcome | Settled by |
| --- | --- | --- |
| R1, request body as a sequence | **Changed:** a span primitive plus a sequence overload that only branches. The built-in host passes contiguous spans; Kestrel passes sequences. | E0 rows for both paths |
| R2, measure then write | **Accepted, sharpened:** one `GetSpan(5 + size)` and one `Advance`, with no staging. Messages over the host's contiguous cap are staged, or reset on error. The rewind buffer is not needed, because a unary method returns before encoding starts. | E0 bytes copied per reply; the `large_unary` interop case |
| R3, owned by default, views opt-in | **Accepted:** views only in a synchronous fast profile, and only with a codec generated later | E0 owned-against-borrowed access on 32- and 512-byte fields |
| R4, pool bytes, not messages | **Accepted.** Per-stream readers and writers are pooled with their stream. | allocation gate |
| R5, codec seam, Google.Protobuf first | **Accepted, with a numeric trigger:** generate a codec from `.proto` only if the codec takes at least 15% of server CPU per call on any representative shape, or its allocations cost at least 3% of throughput. C#-first message contracts were dropped; services stay C#-first. | E0 and the same-host comparison of the two codecs |
| R6, strings copied by default | **Accepted.** The string cache is deferred until a profile shows the allocation. | E0 string-heavy shape |
| R7, batch writes, epochs only for shared state | **Accepted.** One writer per connection owns HPACK and the windows. The TsavoriteLog-style concurrent reservation was **rejected** for milestone 1, because HPACK and window decisions are ordered per connection. Inline execution became "the signature decides", with a host policy and sampled demotion. | E2/E6 flush sweep; E5 for the inline default |

The questions below are settled as follows:

- **Questions 1 to 3** are settled by the table above.
- **Question 4**, scratch retention, is still open.
- **Question 5**, the hand-off cost, became experiments E3 and E4.
- **Question 6**, the flush policy, is set in [CALL-INTERFACE.md](CALL-INTERFACE.md#the-response-path).

## Benchmark controls for any comparison with grpc-dotnet

The protobuf note lists ten controls. The essentials:

- **Same codec on both sides.** Google.Protobuf through the generated marshaller on both sides, with GRPC.NET's own codec reported as a separate row.
- **Message shapes.** Scalar-only, string-heavy, and nested or repeated messages, at 8, 32, 512 and 4096 bytes.
- **Same host.** The same Kestrel version, transport options and HTTP/2 limits, and the grpc-dotnet service registered as a singleton.
- **Same features.** Compression, deadlines, interceptors, logging and tracing match on both sides.
- **Separate load generator.** An out-of-process load generator on separate cores, with fixed connection and stream counts, including a run with one stream per connection. JSON-RPC.NET's September gRPC rows ran the .NET client on the server's cores. Those figures measured the client, and they are not a server baseline.
- **Saturation.** A cell counts only if the server cores are at least 90% busy and doubling the offered load does not change the result, which is ioxide's rule.
- **Validation and true percentiles.** `h2load` counts HTTP 2xx only and reports its maximum where a p99 is expected. Pair every acceptance cell with a standard gRPC client that checks each `grpc-status` and payload, and record a real latency histogram.
- **Calls in flight.** Report the depth: 1 call in flight per connection, and 256 as in the README baselines. FASTER, Garnet and Shadowfax publish their headline figures only with deep client pipelining, so a figure at one depth must not be compared with one at another.
- **Allocation reporting.** Bytes allocated per call and GC counts, not only requests per second, plus a Kestrel floor measured with an endpoint that returns a fixed response.
- **Allocation gate in CI.** Fail a build when allocated bytes per call exceed the expected value, as Garnet's CI does with a 10% margin.
- **Warmup and tiering.** A fixed warmup and the same tiering settings on both sides. Cold Tier-0 code inverts serializer rankings, as JSON-RPC.NET's serializer work found.

## Questions this adds to the design review

1. **`Process` input type.** `ReadOnlySequence<byte>` per R1, or the current `ReadOnlySpan<byte>`.
2. **Message ownership model.** R3: owned by default, ref struct views as an opt-in, and leased `bytes` fields.
3. **Codec seam shape and staging.** R5.
4. **Where per-thread scratch lives.** Whether the core keeps per-thread scratch like JSON-RPC.NET's, and how retained memory is bounded: a cap per buffer, as JSON-RPC.NET's 64 KiB, or a process-wide byte budget, as Garnet's buffer pool enforces.
5. **Hand-off cost under Kestrel.** Before any work on a built-in host, measure the core behind Kestrel against the core behind a minimal socket host that processes inline and flushes once per pass. Run with 1 and 256 calls in flight per connection, and report allocated bytes per call. Add a Kestrel run with `UnsafePreferInlineScheduling`, which shows how much of the gap the transport hop accounts for, and a run with one deliberately slow method, which shows the stall cost of running inline. This repeats Garnet's design study for our case.
6. **Flush policy.** R7: flush at the end of each read pass, with a byte or time threshold for responses that complete outside one.
