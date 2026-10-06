# Research: memory, buffers and zero-allocation marshalling

**Status:** research of 2026-10-05. It informs the open questions in [README.md](README.md#open-design-questions) and [CALL-INTERFACE.md](CALL-INTERFACE.md#open-questions); it decides nothing. The recommendations below are inputs to the design review.

This document summarizes three sets of notes. Each note pins every claim to a source file and line at a recorded commit, a documentation page or a paper, and marks its own inferences.

| Note | Covers |
| --- | --- |
| [research/memory/flatsharp.md](research/memory/flatsharp.md) | FlatSharp (James Courtney): deserialization modes, buffer abstractions, code generation, pooling, gRPC integration, benchmarks |
| [research/memory/protobuf-grpc-dotnet.md](research/memory/protobuf-grpc-dotnet.md) | Google.Protobuf and protobuf-net span APIs, grpc-dotnet's serialization seams and per-call costs, Kestrel's HTTP/2 memory model, string decoding |
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
- **A thread-pool hop.** Each stream is dispatched to the thread pool.
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

## Recommendations for the design review

These are the three notes' inferences, reconciled where they disagreed.

### R1. Accept the request body as a sequence and decode from a span

The three notes disagreed on `Process`'s input:

- **FlatSharp note:** keep the contiguous span the draft has now.
- **Protobuf note:** take `in ReadOnlySequence<byte>`, because a span forces the host to copy any body that spans more than one 4 KB block.
- **Arenas note:** take a span, copied once into per-thread scratch when the body arrives in pieces.

**Reconciled recommendation:** `Process` takes `in ReadOnlySequence<byte>`, as JSON-RPC.NET's endpoint already does.

- When the body is a single segment (`IsSingleSegment`), the core decodes `FirstSpan` directly.
- Otherwise it copies the message once into per-thread scratch, bounded by the maximum message size, and decodes from that.

This keeps the copy out of every host and keeps the codec on contiguous spans, which FlatSharp's experience and protobuf's sequential format both favour. The core reads the 5-byte prefix in place, falling back to a stack copy when the prefix straddles segments.

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

### R5. A codec seam in the core, a Google.Protobuf adapter first, and a built-in codec only if measured

All three notes converge on this staging for README question 3:

1. **The seam.** The core defines a codec interface: decode from a span, report the exact size, and encode into an `IBufferWriter<byte>`. Struct-generic implementations avoid interface dispatch.
2. **First codec.** A Google.Protobuf adapter, built on the public `ParseFrom`, `CalculateSize` and `WriteTo` APIs, gives `.proto` interop from day one.
3. **Built-in codec.** A generated protobuf codec is added only if benchmarks show the savings. It would use a source generator and C#-first contracts in the style of protobuf-net, and it could offer:
   - ref struct views;
   - UTF-8 string accessors;
   - struct messages;
   - cached sizes.

   It would need conformance work comparable to FlatSharp's: round-trip tests against Google.Protobuf, fuzzing, and the gRPC interop suite.

The claim "no allocation per call" belongs to the built-in codec on scalar or view-typed messages. With class-typed messages, the message objects and their strings are the user's choice and are reported as such.

### R6. Strings

Three options, in order of preference:

- **Copy by default.** The default materializes a `string` per field.
- **UTF-8 views.** Views expose UTF-8 bytes, which must still be validated because protobuf requires valid UTF-8 in `string` fields.
- **A cache for repeated values.** For low-cardinality values such as tenant ids or enum-like strings, a cache keyed by the UTF-8 bytes could avoid the allocation itself. Kestrel's reuse of known-header values is the precedent.

No shipping `Utf8String` type exists.

## Benchmark controls for any comparison with grpc-dotnet

The protobuf note lists ten controls. The essentials:

- **Same codec on both sides.** Google.Protobuf through the generated marshaller on both sides, with GRPC.NET's own codec reported as a separate row.
- **Message shapes.** Scalar-only, string-heavy, and nested or repeated messages, at 8, 32, 512 and 4096 bytes.
- **Same host.** The same Kestrel version, transport options and HTTP/2 limits, and the grpc-dotnet service registered as a singleton.
- **Same features.** Compression, deadlines, interceptors, logging and tracing match on both sides.
- **Separate load generator.** An out-of-process load generator on separate cores, with fixed connection and stream counts, including a run with one stream per connection.
- **Allocation reporting.** Bytes allocated per call and GC counts, not only requests per second, plus a Kestrel floor measured with an endpoint that returns a fixed response.
- **Warmup and tiering.** A fixed warmup and the same tiering settings on both sides. Cold Tier-0 code inverts serializer rankings, as JSON-RPC.NET's serializer work found.

## Questions this adds to the design review

1. **`Process` input type.** `ReadOnlySequence<byte>` per R1, or the current `ReadOnlySpan<byte>`.
2. **Message ownership model.** R3: owned by default, ref struct views as an opt-in, and leased `bytes` fields.
3. **Codec seam shape and staging.** R5.
4. **Where per-thread scratch lives.** Whether the core keeps per-thread scratch like JSON-RPC.NET's, and its retention cap.
