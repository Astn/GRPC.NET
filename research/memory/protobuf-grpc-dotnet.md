# Zero-copy Protocol Buffers and gRPC on .NET: prior art for GRPC.NET

Research notes for the GRPC.NET design review (README "Open design questions" 3 and 7, and the unary fast path in CALL-INTERFACE.md). Compiled 2026-10-05.

Every claim in the numbered sections cites a source file and line at a pinned commit, or a public URL. Statements marked **Inference** are my reading of the evidence, not something a source says.

## Sources

| Repository | Commit | Commit date | Notes |
| --- | --- | --- | --- |
| [protocolbuffers/protobuf](https://github.com/protocolbuffers/protobuf/tree/4d99e0f678cbdb2963250c87566f89bbf1718c4c) | `4d99e0f678cbdb2963250c87566f89bbf1718c4c` | 2026-10-06 | `csharp/` tree, the C# code generator and `arena.h`. Google.Protobuf `VersionPrefix` 3.37.0, targets `netstandard2.0;net8.0` ([csproj L8-L11](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/Google.Protobuf.csproj#L8-L11)) |
| [protobuf-net/protobuf-net](https://github.com/protobuf-net/protobuf-net/tree/430dfcc464eabba8d47c36175b7682042c59d05d) | `430dfcc464eabba8d47c36175b7682042c59d05d` | 2026-09-16 | v3 line |
| [protobuf-net/protobuf-net.Grpc](https://github.com/protobuf-net/protobuf-net.Grpc/tree/3471179ce47470ed8858c9f5cb69314c5e00dbcc) | `3471179ce47470ed8858c9f5cb69314c5e00dbcc` | 2026-09-16 | |
| [grpc/grpc-dotnet](https://github.com/grpc/grpc-dotnet/tree/7db6c142a2dd5e0fefb0681e67766e339a15dc47) | `7db6c142a2dd5e0fefb0681e67766e339a15dc47` | 2026-09-17 | `GrpcDotnetVersion` 2.84.0-dev |
| [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore/tree/1c9384556239e201511cadf5ced0bf3f753a98ab) | `1c9384556239e201511cadf5ced0bf3f753a98ab` | 2026-10-05 | `src/Servers/Kestrel`, `src/Shared` |

Link prefixes used below:

- PB = `https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/`
- GPB = PB + `csharp/src/Google.Protobuf/`
- PN = `https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/`
- PG = `https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/`
- GD = `https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/`
- K = `https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/`
- S = `https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/`

Links are written out in full so they work when lifted.

---

## 1. Google.Protobuf: span-based parsing and writing

### 1.1 The seams

- **`IBufferMessage`** extends `IMessage` with two members, `InternalMergeFrom(ref ParseContext)` and `InternalWriteTo(ref WriteContext)`, and its docs say users should never call them directly ([IBufferMessage.cs L16-L29](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/IBufferMessage.cs#L16-L29)).
- **The generator emits `IBufferMessage` on every message** unless `GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE` is defined ([csharp_message.cc L114-L115](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/compiler/csharp/csharp_message.cc#L114-L115), [L661-L664](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/compiler/csharp/csharp_message.cc#L661-L664)).
- **`ParseContext` is a `public ref struct`** holding a `ReadOnlySpan<byte>` and a `ParserInternalState` struct ([ParseContext.cs L24-L30](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParseContext.cs#L24-L30)). Its initializers are `internal`, including the one taking `ReadOnlySequence<byte>`, which sets up a `SegmentedBufferHelper` to walk the segments ([L76-L97](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParseContext.cs#L76-L97)). Application code cannot create a `ParseContext` directly. A request to make the primitives public was closed as not planned ([issue #8036](https://github.com/protocolbuffers/protobuf/issues/8036), closed 2024-03-15).
- **`WriteContext` is the matching `ref struct`** ([WriteContext.cs L24](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WriteContext.cs#L24)). It has an internal initializer over `IBufferWriter<byte>` ([L52](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WriteContext.cs#L52)).
- **Public entry points:**
  - `MessageParser<T>.ParseFrom(ReadOnlySequence<byte>)` and `ParseFrom(ReadOnlySpan<byte>)` ([MessageParser.cs L310-L328](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/MessageParser.cs#L310-L328));
  - `IMessage.MergeFrom(ReadOnlySequence<byte>)` ([MessageExtensions.cs L74](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/MessageExtensions.cs#L74));
  - `IMessage.WriteTo(IBufferWriter<byte>)`, `WriteLengthPrefixedTo(IBufferWriter<byte>)` and `WriteTo(Span<byte>)` ([L150-L198](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/MessageExtensions.cs#L150-L198)). `WriteTo(Span<byte>)` requires the span to be exactly the message size.
- **Fallback for older generated code.** When a nested message does not implement `IBufferMessage`, the parser falls back to copying state into a `CodedInputStream` ([ParsingPrimitivesMessages.cs L207-L233](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitivesMessages.cs#L207-L233)).
- **History.**
  - The span-based parser landed in [protobuf PR #7351](https://github.com/protocolbuffers/protobuf/pull/7351), merged 2020-05-05.
  - The span-based writer landed in [PR #7576](https://github.com/protocolbuffers/protobuf/pull/7576), merged 2020-06-29.
  - gRPC's C# code generator was then switched to these APIs ([grpc/grpc PR #23485](https://github.com/grpc/grpc/pull/23485)).
  - The .NET team's write-up is [gRPC performance improvements in .NET 5](https://devblogs.microsoft.com/dotnet/grpc-performance-improvements-in-net-5/) (J. Newton-King, 2020-10-27). It reports `ToBufferWriter` at about half the time of `ToByteArray`, and parsing from a sequence at about 92% of parsing from a byte array.

### 1.2 Writing to `IBufferWriter<byte>`

- **The first write takes the slow path.** `WriteBufferHelper.Initialize` leaves the span empty, with a TODO noting that the first write goes through the slow path ([WriteBufferHelper.cs L46-L51](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WriteBufferHelper.cs#L46-L51)).
- **Refills.** When the span runs out, the writer calls `Advance(position)` and then `GetSpan()` with no size hint ([L107-L113](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WriteBufferHelper.cs#L107-L113)). On a Kestrel `PipeWriter`, segments are therefore pool-block sized.
- **A nested message is sized again on every write.** `WriteMessage` writes the length prefix from `value.CalculateSize()` ([WritingPrimitivesMessages.cs L26-L28](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WritingPrimitivesMessages.cs#L26-L28)). No cached-size field exists in the C# runtime; a search for `cachedSize` in `csharp/src/Google.Protobuf` finds nothing.
  - **Inference:** a message at depth *d* is sized *d* times plus once at the top, unlike C++ and Java, which cache sizes.
  - gRPC's generated marshaller also calls `CalculateSize()` once for the payload length before `WriteTo` (section 3.1).
- **Strings are measured twice.**
  - `CalculateSize` calls `ComputeStringSize`, which calls `Encoding.GetByteCount` ([CodedOutputStream.ComputeSize.cs L110-L114](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/CodedOutputStream.ComputeSize.cs#L110-L114)).
  - `WriteString` calls `GetByteCount` again, except for strings of 42 characters or fewer when the buffer has room ([WritingPrimitives.cs L153-L170](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WritingPrimitives.cs#L153-L170)).
- **A string that does not fit the current segment is copied through a new array** (`Utf8Encoding.GetBytes(value)`, [L184-L193](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WritingPrimitives.cs#L184-L193)). With 4 KB Kestrel blocks, that happens to any string that straddles a block boundary.

### 1.3 What still allocates per message when parsing

| Item | Allocation | Evidence |
| --- | --- | --- |
| The message object | `ParseFrom` always calls `factory()` | [MessageParser.cs L310-L315](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/MessageParser.cs#L310-L315) |
| Each nested message | Generated code does `new T()` and then `input.ReadMessage(subBuilder)` | e.g. [Struct.pb.cs L819-L823](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WellKnownTypes/Struct.pb.cs#L819-L823) |
| Repeated and map fields | The `RepeatedField<T>` and `MapField<K,V>` objects are allocated by field initializers in every message instance, even when empty | [Struct.pb.cs L145](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WellKnownTypes/Struct.pb.cs#L145), [L897](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WellKnownTypes/Struct.pb.cs#L897) |
| Repeated field backing arrays | Start as a shared empty array and grow by doubling with `new T[]` and a copy | [RepeatedField.cs L39-L42](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/Collections/RepeatedField.cs#L39-L42), [L315-L333](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/Collections/RepeatedField.cs#L315-L333) |
| `string` fields | One `string` per field via `Encoding.GetString`, no cache or interning | [ParsingPrimitives.cs L537-L541](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L537-L541), [L559-L598](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L559-L598) |
| `bytes` fields | A new `byte[]` copy, wrapped in a new `ByteString` | [L547-L551](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L547-L551), [L413-L429](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L413-L429) |
| Unknown fields | An `UnknownFieldSet` only when one appears | [Struct.pb.cs L275](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WellKnownTypes/Struct.pb.cs#L275) |

Why it works this way:

- **Strings.** A proto `string` maps to `System.String`, which must own UTF-16 memory. Decoding uses a strict `UTF8Encoding(throwOnInvalidBytes: true)` ([ParsingPrimitives.cs L29-L30](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L29-L30)).
  - A string split across `ReadOnlySequence` segments is first gathered into a `stackalloc` buffer (256 bytes or less) or an `ArrayPool` buffer, then decoded ([L32](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L32), [L604-L657](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L604-L657)).
  - Only the result string is allocated.
- **`ByteString` is immutable.** It wraps a `ReadOnlyMemory<byte>` ([ByteString.cs L29-L57](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ByteString.cs#L29-L57)), and the parser must copy because the input buffer is not owned by the message.
  - `UnsafeByteOperations.UnsafeWrap(ReadOnlyMemory<byte>)` wraps without copying ([UnsafeByteOperations.cs L52](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/UnsafeByteOperations.cs#L52)), with a warning against mutating the buffer ([L20-L45](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/UnsafeByteOperations.cs#L20-L45)).
  - The parser never uses it. It is a tool for the writing side.

### 1.4 Arenas and object reuse

- **C++** has `Arena`, which allocates messages in large blocks and frees them all at once ([arena.h L217-L221](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/arena.h#L217-L221)).
- **C# has no arena.**
  - The only mention of arenas in `csharp/src/Google.Protobuf` is the generated descriptor for the `cc_enable_arenas` file option ([Descriptor.pb.cs L6794](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/Reflection/Descriptor.pb.cs#L6794)).
  - The request for C# arenas ([issue #3530](https://github.com/protocolbuffers/protobuf/issues/3530)) was closed in 2023. The maintainer judged it a near-complete rewrite of design, generator and library.
- **Object reuse is limited.**
  - `RepeatedField<T>.Clear()` keeps its capacity so that large repeated fields can be reused ([RepeatedField.cs L350-L357](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/Collections/RepeatedField.cs#L350-L357)). This resolved [issue #7828](https://github.com/protocolbuffers/protobuf/issues/7828).
  - Generated messages have no whole-message `Clear()`; the generator emits only per-field `Clear<Field>()` for presence and oneof fields ([csharp_message.cc L247](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/compiler/csharp/csharp_message.cc#L247)). The request to generate one for pooling ([issue #18782](https://github.com/protocolbuffers/protobuf/issues/18782)) was closed as not planned in 2025-09.
  - A caller can `MergeFrom` into an existing instance, but merge semantics append to repeated fields and overwrite singular ones. Reuse therefore needs hand-written resets.

---

## 2. protobuf-net v3

### 2.1 Reader and writer state

- **`ProtoReader.State` and `ProtoWriter.State` are `public ref partial struct`s** ([ProtoReader.State.cs L19](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.State.cs#L19), [ProtoWriter.State.cs L20-L35](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.State.cs#L20-L35)). Each wraps a current `Span`/`Memory` and a reference to a heap `ProtoReader` or `ProtoWriter` that owns the source (Stream, `ReadOnlySequence`, `IBufferWriter`, or the null writer used for measuring).
- **Sources and sinks:**
  - `ProtoReader.State.Create(ReadOnlySequence<byte>, …)` and `Create(ReadOnlyMemory<byte>, …)` ([ProtoReader.ReadOnlySequence.cs L21-L35](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.ReadOnlySequence.cs#L21-L35));
  - `ProtoWriter.State.Create(IBufferWriter<byte>, …)` ([ProtoWriter.BufferWriter.cs L20](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.BufferWriter.cs#L20)).
- **Pooling.** The heap reader and writer objects are pooled by `Pool<T>`: one `[ThreadStatic]` slot plus a locked shared queue of 20 ([Internal/Pool.cs L6-L52](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/Internal/Pool.cs#L6-L52)). The `IBufferWriter` writer is taken from this pool ([ProtoWriter.BufferWriter.cs L29](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.BufferWriter.cs#L29), returned at L56), as is the `ReadOnlySequence` reader ([ProtoReader.ReadOnlySequence.cs L23](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.ReadOnlySequence.cs#L23), L153).
  - **Inference:** steady-state serialization allocates no reader or writer objects, only the user's message graph.
- **Buffer-writer refills** call `IBufferWriter.GetMemory(model.BufferSize)` ([ProtoWriter.BufferWriter.cs L146-L158](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.BufferWriter.cs#L146-L158)).

### 2.2 Nested messages

- **An `IBufferWriter` cannot back-patch a length prefix,** so protobuf-net measures each length-prefixed sub-message first with a `NullProtoWriter`, writes the varint, serializes, and checks that the lengths match ([ProtoWriter.BufferWriter.cs L370-L405](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.BufferWriter.cs#L370-L405)). The start/end sub-item API throws on this writer type ([L427-L434](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.BufferWriter.cs#L427-L434)).
  - **Inference:** this has the same depth-times-size measuring cost as Google.Protobuf's `CalculateSize` per level.
- **`MeasureState<T>`** is a public struct that captures the top-level measured length, so that a caller can learn the payload size and then serialize ([MeasureState.cs L16-L23](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/MeasureState.cs#L16-L23)). protobuf-net.Grpc uses it for gRPC's `SetPayloadLength` (section 2.5).

### 2.3 Strings and bytes

- **Strings.** `ReadString(StringMap map = null)` decodes to a new `string`. The `StringMap` parameter is a placeholder whose class is documented as not yet implemented ([ProtoReader.State.ReadMethods.cs L797-L811](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.State.ReadMethods.cs#L797-L811), [StringMap.cs L6-L9](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/StringMap.cs#L6-L9)).
- **Optional interning** (`InternStrings` on the model) passes the already-allocated string through a per-reader `Dictionary<string,string>` ([ProtoReader.cs L250-L270](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.cs#L250-L270)), which is cleared when the reader is recycled ([L165-L168](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.cs#L165-L168)).
  - **Inference:** this deduplicates repeated values within one payload, for retained memory. It does not avoid the allocation.
- **Strings across segments.** A string split across sequence segments is gathered into a pooled buffer and then decoded ([ProtoReader.ReadOnlySequence.cs L267-L290](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.ReadOnlySequence.cs#L267-L290)).
- **Invalid UTF-8.** The decoder is a default `new UTF8Encoding()` ([ProtoReader.cs L273](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.cs#L273)), so invalid UTF-8 becomes U+FFFD instead of failing. Google.Protobuf throws instead (section 1.3).
- **`Utf8String` support exists only as commented-out mock code** ([Internal/PrimaryTypeProvider.Utf8String.cs](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/Internal/PrimaryTypeProvider.Utf8String.cs)).
- **Writing strings.** A string that does not fit the current buffer goes through an `ArrayPool` rental ([ProtoWriter.BufferWriter.cs L114-L144](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoWriter.BufferWriter.cs#L114-L144)), not a fresh array as in Google.Protobuf.
- **Bytes.** Fields can be `byte[]`, `ArraySegment<byte>`, `Memory<byte>` or `ReadOnlyMemory<byte>` through `IMemoryConverter<TStorage, byte>` ([Serializers/IMemoryConverter.cs L13-L42](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/Serializers/IMemoryConverter.cs#L13-L42)).
  - Reading always calls `converter.Expand` and copies into storage ([ProtoReader.State.ReadMethods.cs L547-L590](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.State.ReadMethods.cs#L547-L590)).
  - A `ReadOnlySequence<byte>` slice overload is commented out ([L636](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/src/protobuf-net.Core/ProtoReader.State.ReadMethods.cs#L636)).
  - **Inference:** a custom converter could rent from a pool, but neither library hands out a view into the input buffer.

### 2.4 Code-first contracts

- **Contracts.** Types are marked `[ProtoContract]` and members `[ProtoMember(n)]` ([README](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/README.md)).
- **Since 3.3, serializers can be generated at build time** for Native AOT, trimming and cold start ([README, "Native AOT and trimming"](https://github.com/protobuf-net/protobuf-net/blob/430dfcc464eabba8d47c36175b7682042c59d05d/README.md); [docs.protobuf-net.dev/aot](https://docs.protobuf-net.dev/aot)).
- **Relevance:** this is the nearest existing model to README open question 1, a C#-first contract with the schema derived from attributes.

### 2.5 protobuf-net.Grpc

- **Service contracts** are interfaces marked `[ServiceContract]` or `[Service]`. Methods may return `T`, `Task<T>` or `ValueTask<T>`, and may take a `CallContext` or a `CancellationToken` ([docs/gettingstarted.md L77-L174](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/docs/gettingstarted.md#L77-L174)). This is close to the JSON-RPC.NET method model.
- **Server hosting is grpc-dotnet.** `AddCodeFirstGrpc` calls `services.AddGrpc()` and registers an `IServiceMethodProvider<T>` ([ServicesExtensions.cs L25-L28](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/src/protobuf-net.Grpc.AspNetCore/ServicesExtensions.cs#L25-L28)). It inherits grpc-dotnet's per-call costs (section 3).
- **Marshaller.**
  - Serializing: `Measure` → `SetPayloadLength` → `Serialize` to `GetBufferWriter()` → `Complete()`, falling back to a right-sized `byte[]` ([ProtoBufMarshallerFactory.cs L181-L215](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/src/protobuf-net.Grpc/Configuration/ProtoBufMarshallerFactory.cs#L181-L215)).
  - Deserializing: from `PayloadAsReadOnlySequence()` ([L218-L225](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/src/protobuf-net.Grpc/Configuration/ProtoBufMarshallerFactory.cs#L218-L225)).
  - It also builds Google.Protobuf marshallers that mirror gRPC's generated code ([GoogleProtobufMarshallerFactory.cs L50-L80](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/src/protobuf-net.Grpc/Configuration/GoogleProtobufMarshallerFactory.cs#L50-L80)).
- **`BytesValue`** is a protobuf-net.Grpc message type over an `ArrayPool`-rented, oversized buffer with an explicit `Recycle()` ([Internal/BytesValue.cs L16-L93](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/src/protobuf-net.Grpc/Internal/BytesValue.cs#L16-L93)). It is a precedent for messages whose lifetime the application must end explicitly.

---

## 3. grpc-dotnet's serialization seams and unary path

### 3.1 `Marshaller<T>`, `SerializationContext`, `DeserializationContext`

- **`Marshaller<T>`** carries `Action<T, SerializationContext>` and `Func<DeserializationContext, T>` ([Marshaller.cs L27-L88](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.Core.Api/Marshaller.cs#L27-L88)).
- **`SerializationContext`** offers `Complete(byte[])`, `GetBufferWriter()`, `SetPayloadLength(int)` and `Complete()` ([SerializationContext.cs L27-L64](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.Core.Api/SerializationContext.cs#L27-L64)).
- **`DeserializationContext`** offers `PayloadLength`, `PayloadAsNewBuffer()` and `PayloadAsReadOnlySequence()` ([DeserializationContext.cs L26-L65](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.Core.Api/DeserializationContext.cs#L26-L65)).
  - Its docs say the sequence is valid only for the duration of the deserializer call, and warn that `PayloadAsNewBuffer` costs a copy plus LOH pressure above about 85 KB.
  - The server implementation returns the stored sequence for the first and `ToArray()` for the second ([DefaultDeserializationContext.cs L25-L49](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Shared/DefaultDeserializationContext.cs#L25-L49)).
- **Code generated by `Grpc.Tools` for Google.Protobuf messages** (`GenerateMarshallerFields` in [grpc/grpc src/compiler/csharp_generator.cc](https://github.com/grpc/grpc/blob/master/src/compiler/csharp_generator.cc)):
  - writes `context.SetPayloadLength(message.CalculateSize())`, then `MessageExtensions.WriteTo(message, context.GetBufferWriter())`, then `context.Complete()`;
  - reads with `parser.ParseFrom(context.PayloadAsReadOnlySequence())`, or `PayloadAsNewBuffer()` for messages without `IBufferMessage`.

  The same sequence appears in protobuf-net.Grpc's comment at [GoogleProtobufMarshallerFactory.cs L57-L64](https://github.com/protobuf-net/protobuf-net.Grpc/blob/3471179ce47470ed8858c9f5cb69314c5e00dbcc/src/protobuf-net.Grpc/Configuration/GoogleProtobufMarshallerFactory.cs#L57-L64).

### 3.2 Reading the request (server)

- **The unary handler** awaits `BodyReader.ReadSingleMessageAsync`, invokes the method, then awaits `BodyWriter.WriteSingleMessageAsync` ([UnaryServerCallHandler.cs L42-L64](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/CallHandlers/UnaryServerCallHandler.cs#L42-L64)).
- **`ReadSingleMessageAsync`** ([PipeExtensions.cs L196-L284](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L196-L284)):
  - It loops on `PipeReader.ReadAsync`.
  - As soon as a whole framed message is in the buffer, it **deserializes immediately while the pipe buffer is still held**: `SetPayload(data)`, then the deserializer, then `SetPayload(null)`, and only then `AdvanceTo` in `finally`.
  - It still loops until `result.IsCompleted` (end of stream), so that it can reject trailing data ([L246-L260](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L246-L260)).
  - A message lives in the pipe only until its deserializer returns.
- **The 5-byte prefix** ([TryReadHeader L145-L171](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L145-L171)) is read from `buffer.First.Span` when the first segment holds five bytes. Otherwise it is copied into a `stackalloc` span. The compressed flag must be 0 or 1, and the big-endian length is checked against `int.MaxValue`.
- **`TryReadMessage`** ([L374-L440](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L374-L440)):
  - It checks `MaxReceiveMessageSize` before the body has arrived.
  - It slices the payload without copying when it is uncompressed.
  - When compressed, it decompresses into a **new `MemoryStream`** and wraps `GetBuffer()` ([L442-L460](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L442-L460)). A comment notes this intermediate array as a performance opportunity (L408).
- **Pooled builders.** The streaming reader uses `PoolingAsyncValueTaskMethodBuilder` ([L294-L295](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L294-L295)); the unary reader does not.

### 3.3 Writing the response (server)

- **`WriteSingleMessageAsync`** ([PipeExtensions.cs L46-L78](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/PipeExtensions.cs#L46-L78)):
  - It calls `HttpResponse.StartAsync()` if the response has not started.
  - It resets the per-call `HttpContextSerializationContext`, points it at `BodyWriter`, and runs the serializer.
  - It does **not** flush. A streamed write flushes unless `WriteFlags.BufferHint` is set (L100-L115).
- **Direct serialization** ([HttpContextSerializationContext.cs L42-L55](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L42-L55), [L140-L179](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L140-L179)) requires that `SetPayloadLength` was called and that compression is off. Then:
  - `GetBufferWriter()` writes the 5-byte header straight into the `PipeWriter` (`GetSpan(5)` and `Advance(5)`, [L124-L138](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L124-L138));
  - it returns the `PipeWriter` itself, so the message is encoded into Kestrel's buffers with **no intermediate copy**.
- **Otherwise the message is encoded twice over.** It goes into an `ArrayBufferWriter<byte>`, sized to the payload length when known and allocated once per serialization context, and is then copied into the pipe ([L169-L178](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L169-L178), [L220-L231](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L220-L231)). Compression adds a `MemoryStream` ([L233-L249](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L233-L249)).
- **Trailers** ([HttpResponseExtensions.cs L25-L47](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpResponseExtensions.cs#L25-L47)):
  - Custom trailers are appended to the HTTP trailers, with binary values base64-encoded into new strings.
  - `grpc-status` is set from cached strings such as `"0"` ([StatusCodeExtensions.cs L25-L30](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/StatusCodeExtensions.cs#L25-L30), [GrpcProtocolHelpers.cs L136-L147](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/GrpcProtocolHelpers.cs#L136-L147)).
  - `grpc-message` is percent-encoded per call when present.

### 3.4 Per-unary-call allocations in grpc-dotnet (server side, above Kestrel)

Verified from source:

1. `HttpContextServerCallContext`, created with `new` per call and stored in `HttpContext.Features` ([ServerCallHandlerBase.cs L64-L65](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/CallHandlers/ServerCallHandlerBase.cs#L64-L65)).
2. `HttpContextSerializationContext` and `DefaultDeserializationContext`, lazily allocated per call context ([HttpContextServerCallContext.cs L62-L69](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextServerCallContext.cs#L62-L69)).
3. A `ServerCallDeadlineManager` when the request carries `grpc-timeout` ([L398-L402](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextServerCallContext.cs#L398-L402)). It has its own cancellation source (L225-L228).
4. A `Metadata` collection for request headers, and one for trailers, only if the method reads them ([L112-L117](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextServerCallContext.cs#L112-L117), [L230-L238](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextServerCallContext.cs#L230-L238)).
5. The service instance.
   - It is resolved from `HttpContext.RequestServices` ([ServerMethodInvokerBase.cs L73-L78](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Shared/Server/ServerMethodInvokerBase.cs#L73-L78)).
   - It is constructed per call when the type is not registered in DI ([DefaultGrpcServiceActivator.cs L28-L38](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/DefaultGrpcServiceActivator.cs#L28-L38)).
   - The handle is a `readonly struct` ([GrpcActivatorHandle.cs L25](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/GrpcActivatorHandle.cs#L25)).
6. The request message graph (section 1.3), the response message, and the `Task<TResponse>` returned by the user's method ([UnaryServerMethodInvoker.cs L95-L135](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Shared/Server/UnaryServerMethodInvoker.cs#L95-L135)).

**Inference:**

- Async state-machine boxes are allocated whenever `ReadAsync`, `StartAsync` or the method do not complete synchronously.
- Resolving `RequestServices` creates the request's DI scope.

### 3.5 Documented performance work

- **[gRPC performance improvements in .NET 5](https://devblogs.microsoft.com/dotnet/grpc-performance-improvements-in-net-5/)** (2020-10-27):
  - per-request allocations on the server fell from about 3.9 KB to about 330 B, with HTTP/2 stream pooling a large part of that;
  - HPACK decoding and response-header compression were improved;
  - Protobuf serialization moved to spans and buffer writers;
  - in a community benchmark the server ranked behind only Rust.
- **[Performance improvements in ASP.NET Core 7](https://devblogs.microsoft.com/dotnet/performance-improvements-in-aspnet-core-7/)** (B. Conroy, 2022-11-01):
  - Kestrel's HTTP/2 output was rewritten from a write lock to a `Channel` with a single consumer ([dotnet/aspnetcore#40925](https://github.com/dotnet/aspnetcore/pull/40925));
  - a gRPC benchmark with 70 streams on each of 28 connections went from 110k to 4.1M RPS;
  - the default HTTP/2 window grew to 768 KB;
  - the socket memory pool was partitioned ([#42237](https://github.com/dotnet/aspnetcore/pull/42237)).
- **[ASP.NET Core updates in .NET 7 Preview 4](https://devblogs.microsoft.com/dotnet/asp-net-core-updates-in-dotnet-7-preview-4/)** describes the same lock-to-queue change.
- **In the repository:**
  - BenchmarkDotNet microbenchmarks of the unary server call handler, with `MemoryDiagnoser` enabled ([perf/Grpc.AspNetCore.Microbenchmarks/Server](https://github.com/grpc/grpc-dotnet/tree/7db6c142a2dd5e0fefb0681e67766e339a15dc47/perf/Grpc.AspNetCore.Microbenchmarks/Server), [DefaultCoreConfig.cs L39](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/perf/Grpc.AspNetCore.Microbenchmarks/DefaultCoreConfig.cs#L39)). Variants: plain, pipelines, compressed, deadline, intercepted and failure status.
  - End-to-end apps and a `QpsWorker` for the gRPC benchmark driver ([perf/benchmarkapps/README.md](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/perf/benchmarkapps/README.md)). Results are published from the aspnet/benchmarks environment.
- **Community benchmark:** [LesnyRumcajs/grpc_bench](https://github.com/LesnyRumcajs/grpc_bench) runs unary calls with capped server and client CPU and RAM, configurable concurrency and connections, and a warmup.

---

## 4. Kestrel's memory model for HTTP/2

### 4.1 `PinnedBlockMemoryPool`

- **Blocks.**
  - The block size is fixed at **4096 bytes** ([PinnedBlockMemoryPool.cs L21](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L21), [L29-L38](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L29-L38)).
  - `Rent` throws for requests above the block size ([L89-L95](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L89-L95)).
  - `Rent` dequeues from a `ConcurrentQueue<MemoryPoolBlock>` or allocates a new block ([L44](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L44), [L103-L120](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L103-L120)).
- **Pinning.** Each block is a `GC.AllocateUninitializedArray<byte>(…, pinned: true)` on the pinned object heap ([MemoryPoolBlock.cs L18-L20](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/MemoryPoolBlock.cs#L18-L20)).
- **Eviction.** Idle blocks are evicted on a 10-second cadence driven by Kestrel's heartbeat ([PinnedBlockMemoryPool.cs L27](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L27), [L149-L180](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L149-L180); [PinnedBlockMemoryPoolFactory.cs L28-L59](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/PinnedBlockMemoryPoolFactory.cs#L28-L59)).
- **Sharing.**
  - The socket transport creates **one pool per I/O queue** and shares it between the input and output pipes of every connection on that queue ([SocketConnectionContextFactory.cs L38-L76](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Transport.Sockets/src/SocketConnectionContextFactory.cs#L38-L76)).
  - The I/O queue count is `min(ProcessorCount, 16)` on Windows or up to 32 cores, and `ProcessorCount / 2` above that ([IOQueue.cs L79-L94](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Transport.Sockets/src/Internal/IOQueue.cs#L79-L94)).
  - The default socket pipe limits are 1 MB read and 64 KB write ([SocketTransportOptions.cs L69](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Transport.Sockets/src/SocketTransportOptions.cs#L69), [L81](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Transport.Sockets/src/SocketTransportOptions.cs#L81)).

### 4.2 How a request body reaches the application

- **The connection parses frames from the socket's input pipe.** For each DATA frame, `Http2Stream.OnDataAsync` **copies** the payload into the stream's own `RequestBodyPipe` (`dataPayload.CopyTo(RequestBodyPipe.Writer)`, [Http2Stream.cs L470](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L470), [L511-L520](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L511-L520)).
- **That pipe uses the same memory pool,** with pause thresholds set just above the flow-control window ([L655-L667](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L655-L667)).
- **`HttpRequest.BodyReader` reads from this per-stream pipe.** Any message larger than one 4 KB block arrives as a multi-segment `ReadOnlySequence`.
- **Each new stream is dispatched to the thread pool** with `ThreadPool.UnsafeQueueUserWorkItem(stream, preferLocal: false)` ([Http2Connection.cs L1384](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L1384)).
- **Streams are pooled per connection** (up to 100, expiring after 5 s, [L65-L67](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L65-L67), [L1494-L1501](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Connection.cs#L1494-L1501)). A reused stream resets its flow control, output producer and body pipe instead of reallocating them ([Http2Stream.cs L65-L86](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L65-L86)).

### 4.3 HPACK and header strings

- **The decoder hands out spans, not strings.**
  - It decodes into reusable `byte[]` scratch buffers ([HPackDecoder.cs L92-L94](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/runtime/Http2/Hpack/HPackDecoder.cs#L92-L94), [L121-L123](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/runtime/Http2/Hpack/HPackDecoder.cs#L121-L123)).
  - It calls the handler with `ReadOnlySpan<byte>` name and value ([IHttpStreamHeadersHandler.cs L6-L13](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/runtime/IHttpStreamHeadersHandler.cs#L6-L13)).
- **Kestrel's `HttpRequestHeaders` then makes strings.**
  - **Known headers** are a fixed list that includes `grpc-timeout`, `grpc-encoding`, `grpc-accept-encoding` and `te` ([HttpHeaders.Generated.cs L62-L92](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http/HttpHeaders.Generated.cs#L62-L92)). Their previous string is reused when the new bytes are identical ASCII (`ReuseHeaderValues`, default true; [HttpRequestHeaders.cs L21-L27](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http/HttpRequestHeaders.cs#L21-L27), [HttpHeaders.Generated.cs L7880-L7913](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http/HttpHeaders.Generated.cs#L7880-L7913)).
  - **Unknown headers,** meaning all custom gRPC metadata, always allocate a value string. The name is interned ([L7917-L7922](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http/HttpHeaders.Generated.cs#L7917-L7922), [HttpRequestHeaders.cs L159-L164](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http/HttpRequestHeaders.cs#L159-L164)).
  - **`:path`** becomes a string with `HeaderPath.ToString()` ([Http2Stream.cs L311-L314](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L311-L314)).
  - **Inference:** a `grpc-timeout` value usually differs per call, so it is a new string each time. A repeated path on the same pooled stream can reuse the previous string.
- **The response encoder works from strings.** The dynamic HPACK encoder takes `string name, string value` ([DynamicHPackEncoder.cs L68](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Hpack/DynamicHPackEncoder.cs#L68)). Repeated pairs such as `content-type: application/grpc` and `grpc-status: 0` become dynamic-table indexes after the first response on a connection. The per-call cost is a hash lookup and comparison, not the bytes.

### 4.4 Response and trailers

- **App writes are buffered per stream.** They go to `Http2OutputProducer`'s per-stream `Pipe` on the same pool ([Http2OutputProducer.cs L68-L70](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2OutputProducer.cs#L68-L70), [L769-L781](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2OutputProducer.cs#L769-L781)).
- **A single writer per connection drains them.**
  - The producer is scheduled onto a bounded `Channel<Http2OutputProducer>`, drained by one `WriteToOutputPipe` loop per connection ([Http2FrameWriter.cs L23-L28](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2FrameWriter.cs#L23-L28), [L153-L182](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2FrameWriter.cs#L153-L182)).
  - The loop **copies** the data into the connection's output pipe behind a 9-byte frame header (`data.CopyTo(_outputWriter)`, [L715-L737](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2FrameWriter.cs#L715-L737)).
- **A completed unary response is written in one pass.** When the app completes with data and trailers, response HEADERS, DATA and trailing HEADERS (END_STREAM) are written under one lock ([L266-L276](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2FrameWriter.cs#L266-L276), [L563-L600](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http2/Http2FrameWriter.cs#L563-L600)).
- **Trailers are known headers too.** `grpc-status` and `grpc-message` are in Kestrel's generated known-header list ([HttpHeaders.Generated.cs L64-L65](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Servers/Kestrel/Core/src/Internal/Http/HttpHeaders.Generated.cs#L64-L65)).

### 4.5 What limits a cheap unary call over Kestrel

Verified, per call:

- **Two copies of the payload in each direction:**
  - request: socket pipe → per-stream body pipe (4.2);
  - response: per-stream output pipe → connection pipe (4.4).
- **One thread-pool dispatch per stream** (4.2).
- **One channel hand-off per response** to the connection's writer loop (4.4).
- **Header work:**
  - HPACK decoding of the request headers;
  - a string per custom metadata value and per changed known header;
  - HPACK encoding of response headers and trailers from strings (4.3).
- **The ASP.NET Core request pipeline** (`HttpContext`, features, endpoint routing) runs for every stream. The .NET 5 write-up puts the remaining per-request cost of Kestrel plus grpc-dotnet at about 330 B (3.5).

**Inference:** none of these are removable from inside a library hosted on Kestrel. The copies and the thread hop sit beneath `BodyReader` and `BodyWriter`. They are the main reason README's "built-in hosting mode only if measured" rule is worth testing.

---

## 5. Strings

Options for avoiding a `string` per protobuf `string` field:

1. **UTF-8 views over the input.** Expose the field as `ReadOnlySpan<byte>`/`ReadOnlyMemory<byte>` (or a small struct wrapping a sequence slice). This costs nothing at parse time but ties the message to the buffer's lifetime (section 6.2), and the UTF-8 must still be validated (`Utf8.IsValid` / `Utf8.ToUtf16` with error checks) to meet protobuf's rule that `string` fields hold valid UTF-8.
2. **A real UTF-8 string type.** .NET's experimental `System.Utf8String` was removed from the runtime in the 5.0 RC1 timeframe and moved to [dotnet/runtimelab](https://github.com/dotnet/runtimelab/issues/3) ([dotnet/runtime#41521](https://github.com/dotnet/runtime/issues/41521)); no shipping type exists. protobuf-net's `Utf8String` provider is commented-out mock code (section 2.3).
3. **Interning or caching.** protobuf-net's `InternStrings` deduplicates after allocating (section 2.3). A cache keyed by the UTF-8 bytes (hash the bytes, compare with an ASCII or UTF-8 check against the cached string, as Kestrel does for header reuse in section 4.3) can avoid the allocation itself for low-cardinality values such as enum-like strings, tenant ids or method names. Neither Google.Protobuf nor protobuf-net ships one; Kestrel's header-value reuse is the closest in-tree precedent.
4. **Lazy decoding.** Keep the byte range and decode on first access. This needs the same lifetime bound as option 1, or a copy to a pooled buffer.

What the libraries do today:

| Library | Decode | Invalid UTF-8 | Split across segments | Encode overflow | Cache or intern |
| --- | --- | --- | --- | --- | --- |
| Google.Protobuf | new `string` per field | throws | stackalloc (256 B or less) or `ArrayPool`, then decode | new `byte[]` | none |
| protobuf-net | new `string` per field | replaced with U+FFFD | pooled buffer, then decode | `ArrayPool` | optional per-payload interning after allocation; `StringMap` not implemented |
| Kestrel headers | new `string`, except reuse of an identical known-header value | n/a | n/a | n/a | previous-value reuse for known headers; header names interned |

---

## Applicability to GRPC.NET

This section maps the findings onto [CALL-INTERFACE.md](../../CALL-INTERFACE.md). Each item is labelled **Fact** (verified above) or **Inference**.

### A. What the unary fast path can and cannot avoid on Kestrel

The draft's `Process(in CallHead, ReadOnlySpan<byte> requestBody, IBufferWriter<byte> responseBody)` promises that synchronous methods complete without allocating per call.

**Cannot be avoided by GRPC.NET when hosted on Kestrel:**

- **Fact:** Kestrel's per-stream body pipe copy, per-stream output pipe copy, thread-pool dispatch, channel hand-off and HPACK work (section 4.5).
- **Fact:** a string per custom metadata value, and per `grpc-timeout` value that changes between calls (4.3). `CallHead.RequestMetadata` "as a view over the host's decoded headers" is therefore a view over strings Kestrel already allocated, not over bytes.
- **Fact:** the `HttpContext`/feature machinery. Its steady-state cost was measured at about 330 B per request including grpc-dotnet in .NET 5 (3.5). That figure predates later releases; GRPC.NET should measure its own Kestrel floor with a no-op endpoint.
- **Inference:** `HttpResponse.StartAsync`, `BodyReader.ReadAsync` when the body is not yet complete, and `FlushAsync` can each suspend. A host adapter written as `async Task` allocates a state machine whenever any of them does. A pooling builder or a hand-written `IValueTaskSource` reduces this; it does not remove it.

**Can be avoided, because grpc-dotnet pays for it (3.4) and the design does not need it:**

- **Fact (grpc-dotnet cost):** a call-context object, serialization and deserialization context objects, and `Metadata` collections.
  - **Inference:** a `readonly struct CallHead` passed by `in`, plus contexts reused per connection or per stream, removes them.
- **Fact (grpc-dotnet cost):** a `Task<TResponse>` per call from the method invoker.
  - **Inference:** a synchronous `Reply Add(Request r)` dispatched through a typed delegate removes it.
- **Fact (grpc-dotnet cost):** per-call service activation when the type is not registered.
  - **Inference:** JSON-RPC.NET's bound-instance model avoids it.
- **Fact (grpc-dotnet cost):** the intermediate `ArrayBufferWriter` when the payload length is unknown.
  - **Inference:** the core always knows the length before the prefix if it measures first. Alternatively, it reserves 5 bytes with `GetSpan(5)` and back-patches them. That works only while the 5 bytes stay in an un-advanced span, so measuring first is the robust choice.
- **Fact (grpc-dotnet cost):** decompression into a `MemoryStream`. Out of scope if compression is deferred (CALL-INTERFACE open question 3).

**Cannot be avoided with today's message types:**

- **Fact:** the request and response message objects, nested messages, repeated-field arrays, strings and `ByteString` copies (1.3, 2.3).
- **Inference:** "no per-call allocation" holds only for messages made of scalars, or for a codec that maps fields to views or pooled objects (B, C).

**Small but real:**

- **Fact:** the 5-byte prefix can be read in place when the first segment holds it, and from a stack copy otherwise (3.2).
- **Inference:** with 4 KB blocks and small unary messages, the split case is rare but must be handled.
- **Inference:** the draft's `ReadOnlySpan<byte> requestBody` forces a contiguous body. A multi-segment body (any message larger than about 4 KB, or a frame that straddles blocks) would have to be copied to a pooled buffer first. Taking `in ReadOnlySequence<byte>` in `Process`, as JSON-RPC.NET's `JsonRpcProcessor.Process(session, in buffer, …)` does ([JsonRpcEndpoint.cs at a87c6b9](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/AustinHarris.JsonRpc.AspNetCore/JsonRpcEndpoint.cs)), avoids that. Google.Protobuf already parses sequences.

### B. Bounding message lifetime over pooled pipe buffers

- **Fact:** grpc-dotnet parses while it holds the pipe buffer and advances only after the deserializer returns. `PayloadAsReadOnlySequence` is documented as valid only during the deserializer call (3.1, 3.2).
- **Fact:** JSON-RPC.NET's endpoint follows the same shape. It reads until `IsCompleted`, processes the whole `ReadOnlySequence` synchronously, writes the reply into `BodyWriter`, flushes, and then calls `AdvanceTo(buffer.End)` ([JsonRpcEndpoint.cs at a87c6b9](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/AustinHarris.JsonRpc.AspNetCore/JsonRpcEndpoint.cs)).
- **Inference: the rule for GRPC.NET.** Any request view (UTF-8 string views, `bytes` slices, lazily decoded sub-messages) is valid only until the method returns, for the synchronous unary path. The core must:
  1. hold the `ReadResult` buffer across deserialize, invoke and serialize, and call `AdvanceTo` only after `Process` returns;
  2. forbid views from escaping into `Task`/`ValueTask` continuations, because the buffer may be recycled to another connection. The memory pool is shared across all connections on an I/O queue (4.1), so a use-after-return corrupts unrelated calls rather than failing fast.
- **Inference:** for async methods and streaming, either deserialize to owned objects (copying strings and bytes), or extend the lease explicitly: copy the message's bytes into a pooled buffer owned by the message, with a `Dispose`/`Recycle` contract. protobuf-net.Grpc's `BytesValue.Recycle()` is a precedent (2.5).
- **Inference:** a `ref struct` view type would let the compiler enforce the synchronous bound, because it cannot be captured by an async method or a lambda. That fits the `Process` fast path and cannot be used by `ProcessAsync`.

### C. Google.Protobuf through `IBufferMessage`, or GRPC.NET's own codec?

**For consuming Google.Protobuf:**

- **Fact:** `ParseFrom(ReadOnlySequence<byte>)` and `WriteTo(IBufferWriter<byte>)` are public and parse or write in place (1.1).
- **Fact:** it is the dependency every `.proto`-generated C# type already has, which matters for interoperability and for the `.proto`-first contract option.
- **Fact:** grpc-dotnet's own generated marshallers use exactly this path. A benchmark against grpc-dotnet using Google.Protobuf therefore isolates the server, not the codec.

**Against consuming it, or for a codec of GRPC.NET's own:**

- **Fact:** the `ParseContext`/`WriteContext` internals are not usable from outside; issue #8036 was closed as not planned (1.1).
- **Fact:** Google.Protobuf cannot avoid message, nested-message, string and `ByteString` allocations. It has no arena and no generated whole-message `Clear` (1.3, 1.4).
- **Fact:** it re-measures nested messages and strings on write, and allocates a new array when a string straddles an output segment (1.2).
- **Inference:** these are exactly the per-call costs that the goal of beating JSON-RPC.NET (16 M+ req/s on raw TCP, README baselines) cannot carry at the core.
- **Inference:** a source-generated codec in the style of jsmn for JSON-RPC.NET could do what neither library does:
  - map `string` fields to UTF-8 views on the fast path;
  - map `bytes` fields to sequence slices;
  - make messages structs, or pooled and reset objects;
  - cache sizes during a single serialize.

  protobuf-net's attribute contracts and build-time generator (2.4) are prior art for the C#-first side of that choice.

**Inference, suggested resolution for open question 3:**

1. Keep the core's codec seam serializer-agnostic: a typed reader over `ReadOnlySequence<byte>` and a writer over `IBufferWriter<byte>` that knows the payload length up front, much like grpc-dotnet's `Marshaller`.
2. Ship a Google.Protobuf adapter through the public `ParseFrom(ReadOnlySequence)`/`CalculateSize`/`WriteTo(IBufferWriter)` APIs for `.proto`-generated types.
3. Reserve the allocation-free claim for a built-in codec on C#-first contracts.

This mirrors JSON-RPC.NET, where jsmn is built in and other serializers are adapters.

### D. What a fair comparison against grpc-dotnet must control for

Inference throughout, grounded in the facts above:

1. **Same codec on both sides.**
   - Run grpc-dotnet with Google.Protobuf through the generated marshaller (`SetPayloadLength` + `GetBufferWriter`, 3.1). Otherwise grpc-dotnet pays an extra `ArrayBufferWriter` copy (3.3).
   - Report a second row with GRPC.NET's own codec, labelled as such.
2. **Same message shape.** Include a scalar-only message, a message with strings, and a message with nested and repeated fields, since the allocation profile differs completely (1.3). A scalar-only benchmark flatters any design.
3. **Same host stack.** GRPC.NET-on-Kestrel against grpc-dotnet-on-Kestrel, same Kestrel version, same transport options (I/O queue count, pipe limits, `UnsafePreferInlineScheduling`), same HTTP/2 limits (`MaxStreamsPerConnection`, window sizes). Report any built-in host separately.
4. **Same service activation.** Register the grpc-dotnet service as a singleton, or the comparison includes per-call DI construction (3.4).
5. **Same features off or on.** No compression, no deadline header unless both honour it (`grpc-timeout` adds a `ServerCallDeadlineManager` and a string, 3.4 and 4.3), no interceptors, logging at Warning, no `EventSource` listeners, and `Activity` tracing off or on for both.
6. **Same metadata.** Custom metadata costs a string per value in Kestrel regardless of server library (4.3). Benchmark with zero and with N custom headers.
7. **Client cost separated.** The README baseline's gRPC rows include the .NET client on the same cores. Use an out-of-process load generator pinned to separate cores (h2load, ghz, or the gRPC `qps_json_driver`), capping server CPUs as [grpc_bench](https://github.com/LesnyRumcajs/grpc_bench) does, so that server throughput is not limited by the client.
8. **Streams per connection and connections.** Kestrel's HTTP/2 throughput is very sensitive to multiplexing (110k against 4.1M RPS on the same benchmark across the .NET 7 change, 3.5). Fix and report both, and include a one-stream-per-connection run.
9. **Allocation and GC reporting.** Report bytes allocated per call (BenchmarkDotNet `MemoryDiagnoser` for the core alone, `dotnet-counters` allocation rate and GC counts end to end), not only RPS. Measure the Kestrel floor with an empty endpoint that echoes a fixed gRPC response, so the core's share is visible.
10. **Warmup and tiering.** Fixed warmup, tiered PGO on or off for both, and the same runtime version. Earlier JSON-RPC.NET serializer work found that cold Tier-0 code inverts rankings.
