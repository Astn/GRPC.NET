# Memory arenas and zero-allocation marshalling: prior art for GRPC.NET

Research notes, 2026-10-05. Scope: how .NET and other ecosystems avoid per-call allocation and copying in serialization and RPC, and what that implies for the GRPC.NET call interface draft ([CALL-INTERFACE.md](../../CALL-INTERFACE.md)). FlatSharp is covered by a separate track and is not repeated here.

Every factual claim carries a source: a GitHub link pinned to a commit (with line numbers), a documentation URL, or a paper. Statements marked **Inference** are the author's reading of that evidence, not something a source states.

## Sources examined

Shallow clones (depth 1, some sparse) taken on 2026-10-05:

| Repository | Commit |
| --- | --- |
| [dotnet/runtime](https://github.com/dotnet/runtime) | `f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7` |
| [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) | `1c9384556239e201511cadf5ced0bf3f753a98ab` |
| [dotnet/orleans](https://github.com/dotnet/orleans) | `1cc211b4c715e1a984d593a4689c9fde334251ea` |
| [microsoft/garnet](https://github.com/microsoft/garnet) | `fc97f951e82e6479bd61bddaa6e9de7f48b79647` |
| [microsoft/FASTER](https://github.com/microsoft/FASTER) | `332ea7da532a688acf648ee60c4f63a1caeaf1f7` |
| [grpc/grpc-dotnet](https://github.com/grpc/grpc-dotnet) | `7db6c142a2dd5e0fefb0681e67766e339a15dc47` |
| [grpc/grpc](https://github.com/grpc/grpc) | `245fd60bf33a75548f17ff00a1d08e491b01c6d8` |
| [protocolbuffers/protobuf](https://github.com/protocolbuffers/protobuf) | `4d99e0f678cbdb2963250c87566f89bbf1718c4c` |
| [capnproto/capnproto](https://github.com/capnproto/capnproto) | `e866bdbaa3078a4bda9e4287d2d4a773c0986902` |
| [c80k/capnproto-dotnetcore](https://github.com/c80k/capnproto-dotnetcore) | `8563c80629c79440f797685e42ba403c4ed5d579` (last commit 2022-02-13) |
| [Cysharp/MemoryPack](https://github.com/Cysharp/MemoryPack) | `bbde522fa3997c1331f1827ec8717b78ecc8de68` |
| [MessagePack-CSharp/MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp) | `242bdad51f89ab69c05791fa32c155ab575e1718` |
| [betwixt-labs/bebop](https://github.com/betwixt-labs/bebop) | `03a2a761d7ea96afdb5bcd8885d0be15cc888e8a` |
| [Tornhoof/SpanJson](https://github.com/Tornhoof/SpanJson) | `bd3394e1b244936291b3847b408280463cecb727` (archived) |
| [fitzgen/bumpalo](https://github.com/fitzgen/bumpalo) | `5a3eefdd18347faf3c8121f2398d322e1589b417` |
| [Astn/JSON-RPC.NET](https://github.com/Astn/JSON-RPC.NET) (baseline) | `a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6` (origin/master) |

Link prefixes used below, to keep lines short:

- `RT` = `https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/`
- `ASP` = `https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/`
- `JR` = `https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/`

Each link in the text is written out in full so it can be clicked.

---

## 0. Baseline: what JSON-RPC.NET already does

GRPC.NET is ported from JSON-RPC.NET, so its existing techniques are the starting point.

- **Per-thread scratch for the synchronous path.** A `[ThreadStatic]` `Scratch` holds a pooled input array, a pooled output writer and a cached reader. A re-entrant call on the same thread gets a fresh instance, which returns its buffers to the pool when done. [JsonRpcProcessor.cs#L326-L360](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/JsonRpcProcessor.cs#L326-L360)
- **Transferable leases for the async path.** `AsyncScratch` is a one-slot thread-static cache in front of a bounded shared array of 64 scratches. A lease that suspends travels with its continuation, and `Return()` caches it on whichever thread completes. Retained input and output buffers are capped at 64 KiB each. [JsonRpcProcessor.Async.cs#L265-L300](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/JsonRpcProcessor.Async.cs#L265-L300)
- **A rewindable pooled output writer.** `PooledByteBufferWriter` implements `IBufferWriter<byte>` over an `ArrayPool<byte>.Shared` array and supports `Rewind(position)`, so a partly written response can be discarded when a method throws. [PooledByteBufferWriter.cs#L7-L33](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/Serialization/PooledByteBufferWriter.cs#L7-L33)
- **Pooled tokenizer storage.** The jsmn tokenizer rents its token array from `ArrayPool<JsmnToken>.Shared` and caches one tokenizer per thread. [JsmnSerializer.cs#L16-L17](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/Jsmn/JsmnSerializer.cs#L16-L17), [JsmnTokenizer.cs#L97-L121](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/Jsmn/JsmnTokenizer.cs#L97-L121)
- **Explicit borrow contracts in the API.** `ProcessAsync(ReadOnlyMemory<byte>)` requires the caller to keep the bytes immutable and valid until the task completes. The `ReadOnlySpan<byte>` overload instead copies into pooled storage, so the caller may reuse its span on return. [JsonRpcProcessor.Async.cs#L49-L61](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/JsonRpcProcessor.Async.cs#L49-L61)

JSON-RPC.NET has no arena for deserialized objects. Method arguments are ordinary GC objects, and the pooling covers bytes, tokens and reader state only.

---

## 1. .NET building blocks

For each type: what it does for a per-call serialization path, what it costs, and how its lifetime goes wrong.

### Span&lt;T&gt; / ReadOnlySpan&lt;T&gt;

- **Use.** A view over contiguous memory: a managed array, `stackalloc` memory or native memory. Parsers and writers can work on any of these through one code path. [Memory&lt;T&gt; and Span&lt;T&gt; usage guidelines](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/memory-t-usage-guidelines)
- **Cost.** None to create. Indexing is bounds-checked unless the JIT can prove the index is in range.
- **Lifetime.** A span can live only on the stack. It cannot be a field of a class, be boxed, or be captured by a lambda. Before C# 13 it could not appear in an async method at all; C# 13 allows ref struct locals in async methods but not across an `await` ([ref struct types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct)). The guidelines' Rule #1 says synchronous APIs should take `Span<T>` because the compiler then enforces the lease. **For GRPC.NET this is the strongest safety tool available:** a borrow that cannot outlive the call.

### Memory&lt;T&gt; / ReadOnlyMemory&lt;T&gt;

- **Use.** A heap-storable handle (object, index, length) to the same kinds of memory. It is needed wherever a buffer crosses an `await` or is stored in a field.
- **Cost.** `.Span` does a type check on the backing object (array, string or `MemoryManager<T>`).
- **Lifetime.** Nothing enforces it. The guidelines rely on convention:
  - Rule #3: a void method must not use the memory after it returns.
  - Rule #4: a Task-returning method must not use it after the task completes.
  - Rules #5 and #6: a type that accepts `Memory<T>` in its constructor or a setter makes its instance methods consumers.
  ([guidelines](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/memory-t-usage-guidelines))

  A `ReadOnlyMemory<byte>` that points into a pooled request buffer and is stored in a user's message object is the textbook use-after-return bug.

### IMemoryOwner&lt;T&gt; and MemoryPool&lt;T&gt;

- **Use.** An explicit owner object whose `Dispose` returns the buffer. Rule #7: an owner must dispose its owner object or transfer it, never both. Rule #8: taking an `IMemoryOwner<T>` parameter means taking ownership.
- **Cost.** `MemoryPool<T>.Shared` is an `ArrayMemoryPool<T>`, and every `Rent` allocates a new `ArrayMemoryPoolBuffer` wrapper around an array rented from `ArrayPool<T>.Shared`. [ArrayMemoryPool.cs#L12-L19](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Memory/src/System/Buffers/ArrayMemoryPool.cs#L12-L19), [ArrayMemoryPool.ArrayMemoryPoolBuffer.cs#L14-L34](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Memory/src/System/Buffers/ArrayMemoryPool.ArrayMemoryPoolBuffer.cs#L14-L34). So the shared `MemoryPool` costs one small allocation per rent. Kestrel's pool avoids this by pooling the owner objects themselves (see §2).
- **Lifetime.** After `Dispose`, `Memory` throws `ObjectDisposedException` for that wrapper (same file, L22). But a `Memory<T>` obtained earlier still points at the array, which may already belong to someone else. The check catches only accesses made through the owner.

### ArrayPool&lt;T&gt;: shared versus custom pools

- **Shared pool design** ([SharedArrayPool.cs](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs)):
  - **Buckets.** 27 power-of-two size buckets (L25). Requests are rounded up to the bucket size: 16 bytes, then doubling up to 1 GiB ([Utilities.cs#L13-L29](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Private.CoreLib/src/System/Buffers/Utilities.cs#L13-L29)).
  - **Rent order.** `Rent` checks a one-array-per-bucket `[ThreadStatic]` cache first, then per-core partitions indexed by `Thread.GetCurrentProcessorId()` (L51-L92, L347-L360). On a miss it allocates, using `GC.AllocateUninitializedArray` for primitive element types other than `bool`, so the array is not zeroed (L111-L116).
  - **Return.** `Return` throws `ArgumentException` if the array length is not exactly a bucket size (L159-L162). It puts the array in the thread-local slot and pushes any previous occupant down to a partition (L164-L171).
  - **Capacity and trimming.** Each partition keeps 32 arrays per size by default; environment variables can change this (L502-L527). Trimming runs on gen2 GC callbacks (L296). Idle arrays are dropped after about 60 s, or 10 s under high memory pressure (L441-L451).
- **Custom pools.** `ArrayPool<T>.Create()` returns a `ConfigurableArrayPool` with a 1 MiB maximum array length and 50 arrays per bucket by default, guarded by a `SpinLock` ([ConfigurableArrayPool.cs#L13-L19](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Private.CoreLib/src/System/Buffers/ConfigurableArrayPool.cs#L13-L19), L167-L208). It has no thread-local cache.
- **Pitfalls.**
  - Rented arrays are larger than requested and carry stale data from earlier renters, possibly uninitialized. Code must never expose bytes past the written length.
  - A double return is not detected. The array then sits in the pool twice and can be handed to two renters at once.
  - A buffer rented on one thread and returned on another lands in the returning thread's TLS slot. That is correct but changes locality.
  - The pool hands out GC heap arrays. They can move unless pinned, which matters for native I/O.

### ReadOnlySequence&lt;T&gt; and SequenceReader&lt;T&gt;

- **Use.** `ReadOnlySequence<T>` represents a message that spans several pooled segments, which is how `PipeReader` delivers request bodies. `IsSingleSegment` and `FirstSpan` allow a fast path when the data is contiguous ([ReadOnlySequence.cs#L41-L55](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Memory/src/System/Buffers/ReadOnlySequence.cs#L41-L55)). `SequenceReader<T>` is a `ref struct` cursor over a sequence ([SequenceReader.cs#L9](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Memory/src/System/Buffers/SequenceReader.cs#L9)).
- **Cost.** Moving across segment boundaries costs branches. A 5-byte gRPC prefix or a varint that straddles two segments needs a slow path or a small copy.
- **Lifetime.** A sequence returned by `PipeReader.ReadAsync` is valid only until `AdvanceTo`, and using it afterwards may corrupt memory (use after free). Helpers must copy any payload before advancing. ([System.IO.Pipelines](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines), PipeReader section and "Memory Corruption" example)

### IBufferWriter&lt;T&gt;

- **Use.** Write in place into the destination's memory: `GetSpan`/`GetMemory`, then `Advance` ([IBufferWriter.cs#L17-L49](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Memory/src/System/Buffers/IBufferWriter.cs#L17-L49)). `PipeWriter` implements it, so a serializer can write directly into Kestrel's output blocks.
- **Cost.** An interface call per `GetSpan`/`Advance`, unless the serializer is generic over a struct or sealed writer type. MemoryPack takes the writer as a generic `ref TBufferWriter` to get devirtualization ([MemoryPackWriter.cs#L18-L52](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/src/MemoryPack.Core/MemoryPackWriter.cs#L18-L52)).
- **Pitfalls.** After `Advance`, the span acquired earlier must not be written again, and a new buffer must be requested ([Pipelines doc](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines), PipeWriter common problems). **Consequence for gRPC:** with a `PipeWriter`, the 5-byte length prefix cannot be reserved, advanced past and back-filled later. The message size has to be known first (a size pass, as Google.Protobuf's `IMessage.CalculateSize` provides, [IMessage.cs#L39](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/IMessage.cs#L39)), or the message has to be staged in scratch and copied. Back-filling is safe only on a writer the core owns, such as JSON-RPC.NET's `PooledByteBufferWriter`.

### ref structs, ref fields and `scoped`

- **Use.** C# 11 added `ref` fields in ref structs and the `scoped` modifier, which limits how far a ref or span parameter can escape ([low-level struct improvements](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-11.0/low-level-struct-improvements)). This makes zero-copy cursors and writers possible:
  - Google.Protobuf's `ParseContext` and `WriteContext` ([ParseContext.cs#L24](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParseContext.cs#L24), [WriteContext.cs#L24](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/WriteContext.cs#L24))
  - MessagePack's `MessagePackReader` and `MessagePackWriter` ([MessagePackReader.cs#L27](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/src/MessagePack/MessagePackReader.cs#L27), [MessagePackWriter.cs#L24](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/src/MessagePack/MessagePackWriter.cs#L24))
  - Orleans' `Writer<TBufferWriter>` and `Reader<TInput>` ([Writer.cs#L130](https://github.com/dotnet/orleans/blob/1cc211b4c715e1a984d593a4689c9fde334251ea/src/Orleans.Serialization/Buffers/Writer.cs#L130), [Reader.cs#L281](https://github.com/dotnet/orleans/blob/1cc211b4c715e1a984d593a4689c9fde334251ea/src/Orleans.Serialization/Buffers/Reader.cs#L281))
- **Cost.** No allocation, and the JIT can keep fields in registers.
- **Lifetime.** Escape is checked at compile time. **Inference:** a generated ref struct view of a request message (fields decoded lazily from the request span) is the only .NET construct that gives a compile-time guarantee that a method cannot keep pooled request bytes. It fits the synchronous unary fast path well and cannot be used across `await`.

### stackalloc

- **Use.** Small fixed-size scratch, such as encoding a status message, formatting `grpc-timeout`, or a small varint buffer. No GC involvement.
- **Cost.** Stack memory is zeroed unless `[SkipLocalsInit]` is applied. SpanJson applies it module-wide ([SkipLocalsInit.cs](https://github.com/Tornhoof/SpanJson/blob/bd3394e1b244936291b3847b408280463cecb727/SpanJson/SkipLocalsInit.cs)).
- **Pitfalls.** Stack overflow ends the process, so sizes must be bounded constants, never request-controlled values. See the [stackalloc reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/stackalloc). With `SkipLocalsInit`, uninitialized stack bytes must never be read.

### Pinned object heap: GC.AllocateUninitializedArray / GC.AllocateArray with `pinned: true`

- **Use.** Long-lived I/O buffers that native code or the kernel can use without `fixed`/`GCHandle` pinning, and that do not fragment the compacting heap. The design doc gives the motivation: long-lived pins left fragmented gen0 in real profiles, so pinned objects get their own heap ([PinnedHeap.md](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/docs/design/features/PinnedHeap.md), lines 19 and 31-41). The API is in [GC.CoreCLR.cs#L849-L904](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/coreclr/System.Private.CoreLib/src/System/GC.CoreCLR.cs#L849-L904).
- **Cost.**
  - The uninitialized path skips zeroing only for types without references. Unpinned arrays under 2048 bytes still go through plain `new T[]` (L853-L864).
  - The design doc restricts POH objects to types without references (L69-L73). It also warns that POH fragmentation resembles LOH fragmentation (L89), because the POH is never compacted.
- **Pitfalls.** POH memory is freed only when unreferenced and collected, so a pool built on it must evict deliberately (see Kestrel in §2).

### NativeMemory.Alloc / AlignedAlloc

- **Use.** Unmanaged arenas outside the GC: bump allocators and aligned blocks for SIMD or DMA. `Alloc` is a thin wrapper over C `malloc` on both Windows and Unix ([NativeMemory.Unix.cs#L120-L145](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeMemory.Unix.cs#L120-L145), [NativeMemory.Windows.cs#L95-L112](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeMemory.Windows.cs#L95-L112)). `AlignedAlloc` is at L25 of each file.
- **Cost.** A `malloc` call per block, invisible to the GC (no GC pressure, but also no GC accounting).
- **Pitfalls.**
  - Use-after-free and double-free are undefined behaviour, not exceptions.
  - It can hold only unmanaged types, so no strings or object references.
  - Reaching it from a `Span<T>` requires `unsafe` code or `MemoryManager<T>`.

### MemoryMarshal and Unsafe

- **Use.** Reinterpreting bytes as typed values without copying:
  - `MemoryMarshal.Cast<TFrom,TTo>`, `AsBytes`, `Read<T>`/`Write<T>`, `GetReference`, `GetArrayDataReference`, `CreateSpan`, and `CreateFromPinnedArray` ([MemoryMarshal.cs#L32-L311](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/MemoryMarshal.cs#L32-L311), L509-L567, L643).
  - `Unsafe.ReadUnaligned`/`WriteUnaligned`/`As`.
  
  In serializers:
  - MemoryPack blits unmanaged structs with `Unsafe.WriteUnaligned` ([MemoryPackSerializer.Serialize.cs#L22-L28](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/src/MemoryPack.Core/MemoryPackSerializer.Serialize.cs#L22-L28)).
  - Bebop reads a `Guid` with `ReadUnaligned` ([BebopReader.cs#L232-L240](https://github.com/betwixt-labs/bebop/blob/03a2a761d7ea96afdb5bcd8885d0be15cc888e8a/Runtime/C%23/Runtime/BebopReader.cs#L232-L240)).
  - capnproto-dotnetcore exposes primitive lists as `MemoryMarshal.Cast<ulong,T>` over the segment ([ListOfPrimitivesDeserializer.cs#L90](https://github.com/c80k/capnproto-dotnetcore/blob/8563c80629c79440f797685e42ba403c4ed5d579/Capnp.Net.Runtime/ListOfPrimitivesDeserializer.cs#L90)).
- **Relevance to protobuf.** The protobuf wire format stores fixed32, fixed64, float and double in little-endian order, and packed repeated fields of these types are contiguous ([Protobuf encoding guide](https://protobuf.dev/programming-guides/encoding/)). **Inference:** packed repeated fixed-width fields can be exposed as `ReadOnlySpan<float>` and similar via `MemoryMarshal.Cast` on little-endian hosts without copying. Varint fields cannot.
- **Pitfalls.**
  - Endianness: MemoryPack documents that its format requires little-endian and its implementation ignores endianness ([README#L1394](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L1394)).
  - Alignment.
  - Struct padding is serialized as-is (MemoryPack [README#L1409](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L1409)).
  - Lengths taken from untrusted input and used in `CreateSpan` or pointer arithmetic remove the bounds checks that make spans safe.

### JIT escape analysis: object stack allocation

- **Use.** The JIT can stack-allocate an object that provably does not escape its method ([object-stack-allocation.md](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/docs/design/coreclr/jit/object-stack-allocation.md), lines 26-46). .NET 10 extends this to small arrays and to delegates ([What's new in the .NET 10 runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)).
- **Limits.**
  - The analysis is mostly within one method. Arguments passed to calls that are not inlined are assumed to escape.
  - Objects with finalizers never qualify (design doc L42-L46, L103-L106).
- **Inference:** temporary objects inside generated codecs may disappear without any work. A message object handed to user code always escapes.

---

## 2. Arena and region patterns in .NET

### There is no GC-managed arena API

- [dotnet/runtime#5633](https://github.com/dotnet/runtime/issues/5633) ("Heap objects with custom allocator and explicit delete") has been open since 2016. A runtime contributor notes in it that about 30 places in the runtime assume a managed pointer leads to a GC heap or segment, which is why foreign allocators are hard to add.
- The September 2025 discussion [dotnet/runtime#120166](https://github.com/dotnet/runtime/discussions/120166) proposed scoped arenas in C#. Replies point out that escape analysis across separately compiled assemblies does not work, because the compiler cannot see callee bodies.
- [dotnet/csharplang#2999](https://github.com/dotnet/csharplang/discussions/2999) asked for a private discardable heap for short isolated computations.
- For protobuf in C#, [protocolbuffers/protobuf#3530](https://github.com/protocolbuffers/protobuf/issues/3530) proposed C# arena allocation in 2017. It was closed in 2023 after a maintainer said it would mean a complete rewrite of the design, the generator and the library.

Note that the .NET GC's own "regions" (the heap organization adopted in .NET 7) are internal to the collector, not an application arena. The PinnedHeap design doc mentions regions only as a GC implementation strategy (L25).

The practical patterns in use instead follow.

### Pattern A: pooled fixed-size blocks on the pinned heap (Kestrel)

- **Blocks.** Each `MemoryPoolBlock` wraps a 4096-byte array allocated with `GC.AllocateUninitializedArray<byte>(length, pinned: true)` and exposes it through `MemoryMarshal.CreateFromPinnedArray` ([MemoryPoolBlock.cs#L10-L20](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/MemoryPoolBlock.cs#L10-L20)).
- **The pool** is a `ConcurrentQueue` of blocks. The block object is the `IMemoryOwner`, so a rent from a warm pool allocates nothing. `Rent` throws for requests larger than 4096 bytes (L91-L94), and a miss allocates a new block ([PinnedBlockMemoryPool.cs#L16-L44](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L16-L44), L89-L120).
- **Eviction is adaptive.** It removes about 1% of blocks when traffic is steady, more when returns exceed rents, and 5% (at least 10) when idle, on a 10-second heartbeat (same file, L27, L176-L215). Evicted blocks are simply unrooted for the GC.
- **History.**
  - Kestrel used 128 KB slabs carved into blocks until [dotnet/aspnetcore#30732](https://github.com/dotnet/aspnetcore/pull/30732) (merged 2021-03-09) replaced them with individual 4K POH arrays. The PR reasons that slabs would need reference counting before blocks could be freed.
  - [#31752](https://github.com/dotnet/aspnetcore/pull/31752) later renamed the pool.
  - [#55890](https://github.com/dotnet/aspnetcore/issues/55890) ("PinnedBlockMemoryPool never releases memory", closed 2025-11) reported the retention failure; the current pool evicts.
- **Diagnostics.** A `DiagnosticMemoryPool` wraps blocks to detect use after return and leaks in tests ([DiagnosticMemoryPool.cs](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/DiagnosticMemoryPool.cs), [DiagnosticPoolBlock.cs](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/DiagnosticPoolBlock.cs)).

### Pattern B: an unmanaged bump allocator (also in ASP.NET Core's shared code)

`UnmanagedBufferAllocator` is a struct that chains `NativeMemory.Alloc` blocks of `4096 - sizeof(void*)` bytes. It hands out uninitialized `Span<T>`/`T*` slices for unmanaged `T` by bumping a pointer, and frees the whole chain in `Dispose` ([UnmanagedBufferAllocator.cs#L9-L39](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/UnmanagedBufferAllocator.cs#L9-L39), L71, L112, L159). This is a true region allocator in .NET, limited to unmanaged data. In the checked-out sparse tree only the file itself references it, so its consumers lie outside the checked-out directories (likely the HTTP.sys interop layer). That attribution is unverified.

### Pattern C: per-session scratch arenas with explicit rewind (Garnet)

- **Buffer pool.** Garnet's `LimitedFixedBufferPool` keeps an array of concurrent queues, level *i* holding buffers of `2^i × sectorSize` ([LimitedFixedBufferPool.cs#L16-L22](https://github.com/microsoft/garnet/blob/fc97f951e82e6479bd61bddaa6e9de7f48b79647/libs/common/Memory/LimitedFixedBufferPool.cs#L16-L22)). It enforces a process-wide byte budget across levels (L27-L53). Each `PoolEntry` is a POH array with a cached raw pointer ([PoolEntry.cs#L37-L43](https://github.com/microsoft/garnet/blob/fc97f951e82e6479bd61bddaa6e9de7f48b79647/libs/common/Memory/PoolEntry.cs#L37-L43)).
- **Scratch allocator.** `ScratchBufferAllocator` is a per-session, single-threaded arena of pinned arrays (`GC.AllocateArray<byte>(length, true)`, L68). It copies arguments in and returns pointer slices. When full it starts a new buffer without copying and keeps the old ones rooted on a stack. Rewinding must happen in reverse allocation order ([ScratchBufferAllocator.cs#L15-L30](https://github.com/microsoft/garnet/blob/fc97f951e82e6479bd61bddaa6e9de7f48b79647/libs/server/ArgSlice/ScratchBufferAllocator.cs#L15-L30)).
- **Scratch builder.** `ScratchBufferBuilder` keeps one contiguous buffer and copies on growth. It documents that earlier slices may then point at memory the GC has freed ([ScratchBufferBuilder.cs#L15-L26](https://github.com/microsoft/garnet/blob/fc97f951e82e6479bd61bddaa6e9de7f48b79647/libs/server/ArgSlice/ScratchBufferBuilder.cs#L15-L26)).
- **In-place parsing.** RESP commands are parsed in place: `SessionParseState` holds pointer-and-length slices into an always-pinned root buffer ([SessionParseState.cs#L17-L47](https://github.com/microsoft/garnet/blob/fc97f951e82e6479bd61bddaa6e9de7f48b79647/libs/server/Resp/Parser/SessionParseState.cs#L17-L47)), and the session keeps a raw `byte* recvBufferPtr` ([RespServerSession.cs#L70-L102](https://github.com/microsoft/garnet/blob/fc97f951e82e6479bd61bddaa6e9de7f48b79647/libs/server/Resp/RespServerSession.cs#L70-L102)).

**Inference:** Garnet trades compile-time safety for speed with raw pointers over pinned memory, and it relies on single-threaded sessions plus documented rewind discipline.

### Pattern D: sector-aligned pinned pools and an epoch-protected log (FASTER)

- `SectorAlignedBufferPool` keeps 32 levels of concurrent queues, with buffers of `sectorSize × 2^level` allocated on the POH, or pinned through `GCHandle` when `UnpinOnReturn` is set ([BufferPool.cs#L172-L191](https://github.com/microsoft/FASTER/blob/332ea7da532a688acf648ee60c4f63a1caeaf1f7/cs/src/core/Utilities/BufferPool.cs#L172-L191), L305-L310).
- The hybrid log allocates pinned, sector-aligned pages ([BlittableAllocator.cs#L179-L203](https://github.com/microsoft/FASTER/blob/332ea7da532a688acf648ee60c4f63a1caeaf1f7/cs/src/core/Allocator/BlittableAllocator.cs#L179-L203)).
- Memory is reclaimed safely through epoch protection (`LightEpoch`, [LightEpoch.cs#L12-L15](https://github.com/microsoft/FASTER/blob/332ea7da532a688acf648ee60c4f63a1caeaf1f7/cs/src/core/Epochs/LightEpoch.cs#L12-L15)): a page is reused only after every thread has left the epoch in which it was retired.

This is region reclamation keyed to time rather than to scope.

### Pattern E: reference-counted, version-checked pages (Orleans ArcBuffer)

- **Writer.** Orleans' `ArcBufferWriter` is an `IBufferWriter<byte>` over pooled pages, specialized for producing `ReadOnlySequence<byte>` ([ArcBufferWriter.cs#L20-L25](https://github.com/dotnet/orleans/blob/1cc211b4c715e1a984d593a4689c9fde334251ea/src/Orleans.Serialization/Buffers/ArcBufferWriter.cs#L20-L25)).
- **Pages.** They come from `ArcBufferPagePool` with a 16 KiB minimum size (L569-L576). Standard-size pages are POH arrays (L657); larger ones come from `ArrayPool` (L666-L680).
- **Reference counting and versions.** Each page carries a reference count and a version. `ArcBuffer` slices hold a token, and every access calls `CheckValidity(token)`. That check throws when the page has been recycled (the version moved on) or when its reference count is zero (L622, L851-L901, L1037-L1062).
- **Session pooling.** Serializer sessions, which hold reference-tracking state, come from a `ConcurrentObjectPool` ([SerializerSessionPool.cs#L12-L47](https://github.com/dotnet/orleans/blob/1cc211b4c715e1a984d593a4689c9fde334251ea/src/Orleans.Serialization/Session/SerializerSessionPool.cs#L12-L47)).

**Inference:** this is the clearest open-source .NET example of a pooled-buffer slice that detects use-after-return at run time, at the cost of a reference-count interlock and a version check per access. Cornflakes (§5) makes the same trade with per-buffer reference counts.

### Pattern F: per-thread scratch with leases (JSON-RPC.NET; MemoryPack; MessagePack)

- JSON-RPC.NET's `Scratch` and `AsyncScratch` (§0).
- MemoryPack keeps `[ThreadStatic]` writer state and option objects ([MemoryPackSerializer.Serialize.cs#L17-L20](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/src/MemoryPack.Core/MemoryPackSerializer.Serialize.cs#L17-L20)) and a reusable linked-array buffer writer whose first buffer is 256 KiB ([ReusableLinkedArrayBufferWriter.cs#L41](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/src/MemoryPack.Core/Internal/ReusableLinkedArrayBufferWriter.cs#L41)).
- MessagePack-CSharp keeps a `SequencePool` of `Sequence<byte>` objects with a 32 KiB minimum span, chosen so the LZ4 worst case stays off the Large Object Heap ([SequencePool.cs#L11-L40](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/src/MessagePack/SequencePool.cs#L11-L40)).

### Pattern G: pooled object graphs and overwrite deserialization

- MemoryPack's `Deserialize(span, ref T value)` overwrites an existing instance where it can ([README#L484-L501](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L484-L501)):
  - It reuses arrays of the same length.
  - It clears and reuses collections that have `Clear()`.
  - It creates new instances for null members and for types with parameterized constructors.

  The README states that `List<T>` is always reused and that `MemoryPoolFormatter<T>` can deserialize a payload into `ArrayPool` memory, which the object returns on `Dispose` ([README#L814-L885](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L814-L885)).
- In C++, gRPC's callback API lets generated code register a `MessageAllocator` per method that supplies request and response objects and gets them back through `MessageHolder::Release()` ([message_allocator.h#L24-L66](https://github.com/grpc/grpc/blob/245fd60bf33a75548f17ff00a1d08e491b01c6d8/include/grpcpp/support/message_allocator.h#L24-L66)). The intent stated there is arena-backed messages.

### Pattern H: struct-of-arrays and blittable records

FASTER's blittable allocator and Garnet's record layout store fixed-layout records in pinned pages and treat them as spans, not object graphs (files above). **Inference:** this suits storage engines. Protobuf messages with optional fields, strings and nested messages do not map onto fixed records, except for the packed numeric arrays noted in §1.

### Pattern I: frozen / NonGC objects

.NET 8 places immortal objects such as string literals and `typeof` results in a NonGC heap that is never scanned or compacted, so the JIT can embed their addresses ([NonGC-Heap.md](https://github.com/dotnet/runtime/blob/f5f66b5f3ceadcd7daca3098b48ba5c8930f01f7/docs/design/features/NonGC-Heap.md), L18 and later). Applications cannot allocate there. **Inference:** for GRPC.NET the useful part is that `static readonly` UTF-8 literals for header names, method paths and status strings (`"grpc-status"u8`) cost nothing per call.

### Research prototypes on .NET

- **Broom** (HotOS 2015) extended the Bartok C# compiler with three region kinds and found GC added up to 40% to data-processing task runtime. Regions cut emulated Naiad vertex runtime by 34%. The three kinds ([paper](https://www.usenix.org/system/files/conference/hotos15/hotos15-paper-gog.pdf)):
  - transferable regions for messages, owned by one actor at a time;
  - actor-scoped regions;
  - lexically scoped temporary regions.
- **Project Snowflake** (OOPSLA 2017) added a manual heap to CoreCLR with unique owners and thread-local "shields". Freed objects are reclaimed through an epoch protocol, and use after free raises exceptions rather than undefined behaviour. It reported up to 3x lower peak working set and 2x runtime improvement ([extended PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2017/07/snowflake-extended.pdf), [ACM](https://dl.acm.org/doi/10.1145/3141879)). Neither shipped in .NET.

---

## 3. Arenas in other ecosystems

### C++ protobuf Arena

- **What it saves.** The class comment says arena allocation gathers allocations into larger blocks and frees them all at once, and that allocation may be thread-safe while destruction is not ([arena.h#L217-L224](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/arena.h#L217-L224)). The docs describe allocation as a pointer increment and deallocation as almost free, with better cache locality ([C++ Arena Allocation Guide](https://protobuf.dev/reference/cpp/arenas/)).
- **Tuning.** `ArenaOptions` accepts:
  - a caller-supplied initial block, which must outlive the arena;
  - start and maximum block sizes (defaults 256 B and 32 KiB, [arena_allocation_policy.h#L27-L28](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/arena_allocation_policy.h#L27-L28));
  - custom block alloc/free hooks ([arena.h#L169-L213](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/src/google/protobuf/arena.h#L169-L213)).

  `Reset()` frees everything after running registered destructors and is not thread-safe (L464-L473). `OwnDestructor` registers cleanup for non-trivial objects (L490-L505).
- **Constraints** ([Arena guide](https://protobuf.dev/reference/cpp/arenas/)):
  - `Swap` and `set_allocated_*` deep-copy when the two messages live on different arenas.
  - `release_*` copies to the heap.
  - Moving an arena message is a deep copy.
  - Messages must not outlive their arena.
- **Use in gRPC.** gRPC C++ core gives every call an `Arena` that tracks how much it allocated so later arenas can pre-size, plus `MakePooled` free lists that round sizes up ([arena.h#L19-L23, L156-L292](https://github.com/grpc/grpc/blob/245fd60bf33a75548f17ff00a1d08e491b01c6d8/src/core/lib/resource_quota/arena.h#L19-L23)).
- **upb.** The C runtime behind the Python, Ruby, PHP and Rust protobuf implementations is arena-only. `upb_Arena_Fuse` joins two arenas' lifetimes so neither is freed until both are released, and `upb_Arena_IncRefFor` adds references ([upb/mem/arena.h#L39-L79](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/upb/mem/arena.h#L39-L79)). The Rust crate wraps it in `rust/upb/arena.rs` ([link](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/rust/upb/arena.rs)).
- **C# has no arena.** Google.Protobuf C# parses spans through `ParseContext`. A `bytes` field is materialized with `ReadRawBytes`, which copies into a new `byte[]` that is then wrapped without a further copy ([ParsingPrimitives.cs#L547-L551](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/ParsingPrimitives.cs#L547-L551)). `UnsafeByteOperations.UnsafeWrap` avoids the copy on the write side and warns that mutating the buffer afterwards breaks serialization, immutability and hashing ([UnsafeByteOperations.cs#L20-L51](https://github.com/protocolbuffers/protobuf/blob/4d99e0f678cbdb2963250c87566f89bbf1718c4c/csharp/src/Google.Protobuf/UnsafeByteOperations.cs#L20-L51)).

### Cap'n Proto: segments as the arena, zero-copy reads

- **Format.** A message is one or more segments, each a flat byte blob, with pointers that are relative within a segment and use a far-pointer form between segments. Extra segments let a builder grow without relocating data ([encoding.md](https://github.com/capnproto/capnproto/blob/e866bdbaa3078a4bda9e4287d2d4a773c0986902/doc/encoding.md), L17-L33). The project describes its objects as always allocated arena-style ([index.md#L72-L74](https://github.com/capnproto/capnproto/blob/e866bdbaa3078a4bda9e4287d2d4a773c0986902/doc/index.md#L72-L74)).
- **Builder.** `MallocMessageBuilder` defaults to a 1024-word first segment that grows heuristically. One constructor reuses a caller-provided, zeroed first segment, and the destructor zeroes what was used, so a tight loop can recycle it ([message.h#L379-L411](https://github.com/capnproto/capnproto/blob/e866bdbaa3078a4bda9e4287d2d4a773c0986902/c%2B%2B/src/capnp/message.h#L379-L411)).
- **Reads.** Reads are zero-copy accessors over the segments. Because multiple pointers can alias one location, readers enforce a traversal limit (default 8M words, 64 MiB) and a nesting limit (64) against amplification attacks ([message.h#L51-L75](https://github.com/capnproto/capnproto/blob/e866bdbaa3078a4bda9e4287d2d4a773c0986902/c%2B%2B/src/capnp/message.h#L51-L75)).
- **.NET.** The listed C# implementation is [capnproto-dotnetcore](https://github.com/c80k/capnproto-dotnetcore) ([otherlang.md#L17](https://github.com/capnproto/capnproto/blob/e866bdbaa3078a4bda9e4287d2d4a773c0986902/doc/otherlang.md#L17)), last committed in February 2022.
  - It allocates each segment as `new ulong[size]` with no pooling ([SegmentAllocator.cs#L122-L124](https://github.com/c80k/capnproto-dotnetcore/blob/8563c80629c79440f797685e42ba403c4ed5d579/Capnp.Net.Runtime/SegmentAllocator.cs#L122-L124), [Framing.cs#L69](https://github.com/c80k/capnproto-dotnetcore/blob/8563c80629c79440f797685e42ba403c4ed5d579/Capnp.Net.Runtime/Framing.cs#L69)).
  - Its generated "domain classes" give up zero-copy for convenience, and it lists mmap, packing and compression as unimplemented ([README#L76-L92](https://github.com/c80k/capnproto-dotnetcore/blob/8563c80629c79440f797685e42ba403c4ed5d579/README.md#L76-L92)).

### Rust: bumpalo and borrow-checked arenas

- **bumpalo.** It bump-allocates within chunks and frees everything at once on reset. It is suited to phase-oriented allocation, and by default it does not run `Drop` for arena values; `bumpalo::boxed::Box` opts back in ([README#L12-L41](https://github.com/fitzgen/bumpalo/blob/5a3eefdd18347faf3c8121f2398d322e1589b417/README.md#L12-L41), L111-L150).
- **Inference:** the big difference from .NET is that Rust lifetimes make it a compile-time error to keep a reference into the arena past its reset. The closest .NET equivalent is a ref struct view used within one synchronous call.
- **Other runtimes.**
  - Go's arena experiment ([golang/go#51317](https://github.com/golang/go/issues/51317)) is on hold indefinitely over API concerns: arena parameters spread virally through APIs, and manual lifetimes conflict with Go's safety promise. A follow-up regions idea is [#70257](https://github.com/golang/go/issues/70257).
  - Java's Foreign Function & Memory API ([JEP 454](https://openjdk.org/jeps/454), final in Java 22) is the managed-runtime design closest to an arena that is safe to ship. An `Arena` owns off-heap `MemorySegment`s with spatial and temporal bounds checks. Confined arenas are single-thread, shared arenas are multi-thread, and closing an arena invalidates its segments so later access throws instead of reading freed memory.

---

## 4. Zero-allocation and zero-copy .NET serializers other than FlatSharp

| Library | Output | Input | Copy and allocation avoidance | Documented limits |
| --- | --- | --- | --- | --- |
| **MemoryPack** | `IBufferWriter<byte>` (generic, by `ref`), `byte[]`, `Stream` | `ReadOnlySpan<byte>`, `ReadOnlySequence<byte>`, `Stream` | "Zero-encoding": copies C# memory directly; unmanaged structs and arrays of them are blitted ([README#L17](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L17), [L92](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L92); [Serialize.cs#L22-L56](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/src/MemoryPack.Core/MemoryPackSerializer.Serialize.cs#L22-L56)); overwrite deserialization; pooled `Memory<T>` members; strings as UTF-8 (default) or UTF-16, which is faster but larger ([README#L456-L462](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L456-L462)) | Little-endian only; padding serialized; limited schema evolution (no deleting or reordering members, unmanaged structs frozen) ([README#L503-L510](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L503-L510)); `Stream` APIs buffer the whole message in a pool first ([README#L450](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L450), [L480](https://github.com/Cysharp/MemoryPack/blob/bbde522fa3997c1331f1827ec8717b78ecc8de68/README.md#L480)) |
| **MessagePack-CSharp** | `IBufferWriter<byte>` | `ReadOnlySequence<byte>`, `ReadOnlyMemory<byte>` | Pooled buffers; ref struct reader and writer; string-key lookup on raw UTF-8 bytes without decoding ([README#L691-L703](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/README.md#L691-L703), [L730](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/README.md#L730)); `ReadBytes`, `ReadStringSequence` and `TryReadStringSpan` return slices of the input with no copy ([MessagePackReader.cs#L740-L805](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/src/MessagePack/MessagePackReader.cs#L740-L805)); optional string interning ([README#L764-L805](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/README.md#L764-L805)); blit formatters for Unity vector arrays | The `byte[]` result is copied out of the internal pool; `Stream` deserialization first builds a sequence ([README#L984-L986](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/README.md#L984-L986)); the high-level API allocates only the result when it is under 64K ([README#L1091-L1093](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/README.md#L1091-L1093)); the README reports zero allocations during deserialization in its micro-benchmark ([L726](https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/242bdad51f89ab69c05791fa32c155ab575e1718/README.md#L726)) |
| **Bebop** (C# runtime) | `BebopWriter` ref struct over a `Span<byte>` | `BebopReader` ref struct over `ReadOnlySpan<byte>` | Fixed-width little-endian reads with `ReadUnaligned`; no reflection | Growth allocates a new `byte[]` and copies, with no pooling ([BebopWriter.cs#L132-L150](https://github.com/betwixt-labs/bebop/blob/03a2a761d7ea96afdb5bcd8885d0be15cc888e8a/Runtime/C%23/Runtime/BebopWriter.cs#L132-L150)); `ToArray` copies (L103-L109); `ReadBytes` copies with `ToArray` ([BebopReader.cs#L256-L266](https://github.com/betwixt-labs/bebop/blob/03a2a761d7ea96afdb5bcd8885d0be15cc888e8a/Runtime/C%23/Runtime/BebopReader.cs#L256-L266)); strings always decoded to `string` (L205-L230) |
| **SpanJson** | `ArraySegment` rented from `ArrayPool`, which the caller must return ([README#L64-L68](https://github.com/Tornhoof/SpanJson/blob/bd3394e1b244936291b3847b408280463cecb727/README.md#L64-L68)) | UTF-8 or UTF-16 spans | Generic over `byte`/`char` symbol type; pooled writer growth ([JsonWriter.cs#L9-L84](https://github.com/Tornhoof/SpanJson/blob/bd3394e1b244936291b3847b408280463cecb727/SpanJson/JsonWriter.cs#L9-L84)); `ReadUtf8StringSpan` returns a span with no allocation ([JsonReader.Utf8.cs#L709](https://github.com/Tornhoof/SpanJson/blob/bd3394e1b244936291b3847b408280463cecb727/SpanJson/JsonReader.Utf8.cs#L709)); module-wide `SkipLocalsInit` | Archived, no further development ([README#L1](https://github.com/Tornhoof/SpanJson/blob/bd3394e1b244936291b3847b408280463cecb727/README.md#L1)) |
| **Orleans.Serialization** | `Writer<TBufferWriter>` ref struct over any `IBufferWriter<byte>`; `ArcBufferWriter` | `Reader<TInput>` ref struct | Pooled sessions; reference-counted, versioned pages (§2E) | Wire format is Orleans-specific |
| **capnproto-dotnetcore** | Segment lists | Segments (`Memory<ulong>`) | Zero-copy readers; primitive lists via `MemoryMarshal.Cast` | Unpooled segments, domain classes copy, inactive since 2022 (§3) |
| **Google.Protobuf (C#)**, reference point | `IBufferWriter<byte>` via `WriteContext` | `ReadOnlySequence<byte>`/span via `ParseContext` | Span-based parsing; `UnsafeWrap` for zero-copy `bytes` on write | Every message, string and `bytes` field is a fresh GC object on parse (§3); C# arena support declined ([#3530](https://github.com/protocolbuffers/protobuf/issues/3530)) |

The current server baseline, grpc-dotnet, hands the deserializer a `ReadOnlySequence<byte>` from a `DeserializationContext` that is reused per call ([DefaultDeserializationContext.cs#L25-L49](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Shared/DefaultDeserializationContext.cs#L25-L49), [HttpContextServerCallContext.cs#L44-L68](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextServerCallContext.cs#L44-L68)). On the response side it may serialize into an `ArrayBufferWriter<byte>` sized to the payload before framing ([HttpContextSerializationContext.cs#L140-L175](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/src/Grpc.AspNetCore.Server/Internal/HttpContextSerializationContext.cs#L140-L175)).

**Common threads (inference):**

1. Every library that reaches zero allocation on write does it by writing into a caller-supplied `IBufferWriter<byte>` and taking the writer as a generic or ref struct to avoid interface dispatch.
2. Zero-copy reads exist only as explicit opt-in span or sequence accessors: MessagePack `TryReadStringSpan`, SpanJson `ReadUtf8StringSpan`, Cap'n Proto readers. Default object materialization still allocates strings and arrays.
3. The only general way to reduce object allocation on read is reuse (overwrite or pooled graphs), and the libraries document its limits.

---

## 5. Research literature

| Paper | What it measured | Implication for a managed runtime |
| --- | --- | --- |
| Kanev et al., *Profiling a Warehouse-Scale Computer*, ISCA 2015. [Google Research](https://research.google/pubs/pub44271/) | Over 20,000 Google machines across three years: "datacenter tax" took 22-27% of all cycles. Its components are protobuf management, RPC, hashing, compression, memory allocation and data movement (memmove). Allocation is named as a substantial component. | Allocation and memmove are taxes paid on every call. Removing them from the RPC layer is worthwhile even when the payload logic is cheap. |
| Karandikar et al., *A Hardware Accelerator for Protocol Buffers*, MICRO 2021. [preprint](https://sagark.org/assets/pubs/protoacc-micro2021-preprint.pdf), [ACM](https://dl.acm.org/doi/10.1145/3466752.3480051) | Protobuf operations took 9.6% of Google fleet cycles, 88% of them in C++. Deserialization alone was 2.2% of fleet cycles and serialization 1.25%. Only 16.3% of deserialization and 35.2% of serialization cycles came from the RPC stack. 24% of messages are 8 bytes or less, 56% are 32 bytes or less, and 93% are 512 bytes or less. The accelerator averaged 6.2x to 11.2x faster than a RISC-V core and 3.8x faster than a Xeon. It notes arena allocation reduces construction and destruction overheads (§2.3). | Most RPC messages are tiny, so fixed per-call costs (allocating the message object, a `byte[]`, a `Task`, a dictionary) dominate over per-byte costs. A GRPC.NET fast path should be judged on 8-to-512-byte messages. |
| Raghavan, Levis, Zaharia, Zhang, *Breakfast of Champions: Towards Zero-Copy Serialization with NIC Scatter-Gather*, HotOS 2021. [PDF](https://people.eecs.berkeley.edu/~matei/papers/2021/hotos_serialization.pdf) | Argues microsecond I/O makes serialization's data movement the bottleneck, and that commodity NIC scatter-gather can support zero-copy, zero-allocation serialization. | Background for Cornflakes. |
| Raghavan et al., *Cornflakes: Zero-Copy Serialization for Microsecond-Scale Networking*, SOSP 2023. [PDF](https://amyousterhout.com/papers/cornflakes_sosp23.pdf), [DOI](https://dx.doi.org/10.1145/3600006.3613137) | A hybrid serializer in Rust that uses NIC scatter-gather only for fields of at least 512 bytes and copies smaller ones, because reference-count bookkeeping and cache misses cost more than copying small fields. Zero-copy buffers are reference-counted `RcBuf`s in DMA-safe pinned memory and held until transmission completes, for use-after-free safety. Results: within 3% of, and up to 128% better than, general-purpose libraries in a key-value store; 15.4% over prior software approaches on a Twitter cache trace; 8.8% over Redis's own serialization inside Redis. Hybrid beat scatter-gather-only by 1.4-14.0%. | Zero-copy pays only above a size threshold. Below it, a tight copy into a pooled output block wins. A managed server should copy small `bytes`/`string` fields and consider references (for example `ReadOnlyMemory<byte>` slices or reference-counted pages) only for large payloads. |
| Wolnikowski, Ibanez, Stone, Kim, Manohar, Soulé, *Zerializer: Towards Zero-Copy Serialization*, HotOS 2021. [ACM](https://dl.acm.org/doi/10.1145/3458336.3465283) | Proposes moving serialization into the DMA path with dedicated hardware, to remove the CPU copy between objects and socket buffers. | Hardware direction only. It confirms the copy into the socket buffer is the cost to target. |
| Pourhabibi et al., *Optimus Prime: Accelerating Data Transformation in Servers*, ASPLOS 2020. [PDF](https://infoscience.epfl.ch/record/274129/files/asplos20-op-pourhabibi.pdf) | Data transformation (protobuf/Thrift) becomes a microservice bottleneck as other latencies shrink. The accelerator is up to about 60x faster than a core on 300-byte protobuf messages. | Again, the small-message regime dominates. |
| Jang et al., *A Specialized Architecture for Object Serialization with Applications to Big Data Analytics* (Cereal), ISCA 2020. [PDF](https://taejunham.github.io/data/cereal_isca2020.pdf) | Hardware serialization for Java objects: 43.4x the average throughput of 88 libraries, and 7.97x/4.81x over Java built-in serialization and Kryo in Spark. | Object-graph serialization in managed runtimes is costly because it chases pointers. Flat layouts help. |
| Sriraman, Dhanotia, *Accelerometer*, ASPLOS 2020. [ACM](https://dl.acm.org/doi/10.1145/3373376.3378450) | At Facebook, microservices spent as little as 18% of cycles in application logic; serialization, I/O, compression and similar overheads took the rest. Cornflakes cites it for serialization at 6.7% of cycles in seven key services. | The RPC orchestration path, not the handler, is where cycles go. |
| Gog et al., *Broom: Sweeping Out Garbage Collection from Big Data Systems*, HotOS 2015. [PDF](https://www.usenix.org/system/files/conference/hotos15/hotos15-paper-gog.pdf) | In C#/Naiad (Mono), GC added up to 40% to task runtime. Regions in the Bartok compiler (transferable message regions, actor-scoped, temporary) cut emulated vertex runtime by 34%. | Message lifetimes in dataflow and RPC systems match regions well. Without runtime support .NET has to emulate them with pooling. |
| Parkinson et al., *Project Snowflake: Non-blocking Safe Manual Memory Management in .NET*, OOPSLA 2017. [PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2017/07/snowflake-extended.pdf) | A manual heap in CoreCLR with owners, shields and epoch reclamation; use after free raises exceptions. Up to 3x lower peak working set and 2x faster runtime. | Shows epoch- and owner-based safety for manually managed .NET objects is feasible, but it is a runtime change. Library code can only imitate it (Orleans version tokens, FASTER epochs). |
| Kolokasis et al., *TeraHeap* (arXiv [2111.10589](https://arxiv.org/abs/2111.10589), originally titled *Garbage Collection or Serialization? Between a Rock and a Hard Place!*) | In Spark and Giraph on the JVM, GC plus serialization/deserialization took up to 87% of execution time. A second heap without serialization improved performance by up to 73%. | Managed runtimes pay twice, once to serialize and again to collect the result. Avoiding materialized object graphs helps on both counts. |
| Tofte, Talpin, *Region-Based Memory Management*, Information and Computation 132(2), 1997. [DOI](https://doi.org/10.1006/inco.1996.2613); Gay, Aiken, *Memory Management with Explicit Regions*, PLDI 1998. [DOI](https://doi.org/10.1145/277650.277748) | Foundations: inferred lexical regions (ML Kit) and explicit, safety-checked regions in C. | The theory behind "free the call's memory at once". |
| Berger, Zorn, McKinley, *Reconsidering Custom Memory Allocation*, OOPSLA 2002. [MSR page](https://www.microsoft.com/en-us/research/publication/reconsidering-custom-memory-allocation/) | Across eight programs with custom allocators, a good general-purpose allocator matched or beat six of them. The two that beat it used regions (up to 44% faster). Proposes "reaps", regions that also allow freeing individual objects. | Generic pooling seldom beats a good allocator, or in .NET a gen0 bump allocation. Region semantics (bulk free tied to a lifetime) are what pays. |

**Inference from the literature as a whole:** the measured costs fall into three groups.

1. Fixed per-call overhead on tiny messages: allocation, construction, destruction.
2. Copies of large byte fields.
3. GC work proportional to the objects that survive.

In .NET, gen0 allocation is itself a pointer bump, so a short-lived message object is cheap to allocate. The cost shows up later as GC frequency and as surviving objects promoted when calls overlap a collection. This is why the JSON-RPC.NET fast path targets *zero* allocations, not *fewer*.

---

## 6. Applicability to GRPC.NET

This section maps the findings onto the [CALL-INTERFACE.md](../../CALL-INTERFACE.md) draft: `Process(in CallHead, ReadOnlySpan<byte> requestBody, IBufferWriter<byte> responseBody)` for the synchronous unary fast path, and `ProcessAsync(CallHead, PipeReader, PipeWriter, ICallEvents)` for everything else.

### 6.1 Verified facts that constrain the design

1. A `ReadOnlySequence<byte>` from `PipeReader.ReadAsync` is invalid after `AdvanceTo` ([Pipelines doc](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)). Under Kestrel those segments are 4 KiB pinned blocks from a pool shared by the connection ([PinnedBlockMemoryPool.cs#L21](https://github.com/dotnet/aspnetcore/blob/1c9384556239e201511cadf5ced0bf3f753a98ab/src/Shared/Buffers.MemoryPool/PinnedBlockMemoryPool.cs#L21)).
2. After `PipeWriter.Advance`, earlier spans must not be written ([Pipelines doc](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)). So the gRPC 5-byte prefix needs the message size first, or staging in a buffer the core owns.
3. `Span<T>` and ref struct parameters cannot escape a synchronous method or survive an `await`. This is enforced by the compiler ([ref struct types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct), [usage guidelines](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/memory-t-usage-guidelines) Rule #1).
4. `Memory<T>` lifetimes are enforced only by convention (guidelines Rules #3 to #8). `ArrayPool` does not detect double returns or use after return (SharedArrayPool source, §1).
5. .NET has no supported managed arena. The relevant proposals are open or were rejected (§2).
6. Most production protobuf messages are small: 93% are 512 bytes or less at Google (ProtoAcc). Zero-copy beats copying only from about 512 bytes up (Cornflakes).
7. JSON-RPC.NET already reaches zero per-call allocation on its synchronous path with per-thread scratch and pooled writers, and offers transferable leases for async (§0).

### 6.2 Recommended per-call memory model (inference)

| What | Where it lives | Rationale |
| --- | --- | --- |
| `CallHead`, parse cursor, writer state, varint and prefix scratch, status formatting | **Stack**: `readonly struct` passed by `in`; ref struct cursors; `stackalloc` of a small constant size under `[SkipLocalsInit]` | No allocation, compiler-checked lifetime. Matches Google.Protobuf `ParseContext` and MessagePack's ref struct reader and writer. |
| Method path and fixed header names | **Static UTF-8 literals** (`"grpc-status"u8`) and a precomputed path-to-method table keyed by bytes | No per-call strings. `Resolve(ReadOnlySpan<byte>)` already takes bytes. |
| Request body, unary fast path | **Borrowed from the host** as `ReadOnlySpan<byte>` for the duration of `Process` | If the host's body is a single segment (`IsSingleSegment`), no copy. Otherwise the host or core copies it once into **per-thread scratch** (JSON-RPC.NET `Scratch.Input`), bounded by the message size limit. |
| Response bytes | Written **directly into the host's `IBufferWriter<byte>`**, with the size computed before writing the 5-byte prefix. On the async path, staged in per-thread or leased scratch (`PooledByteBufferWriter` with `Rewind`) when the method may throw after partly writing. | Rewind gives clean trailers-only errors (as JSON-RPC.NET does now). A size pass avoids back-filling a `PipeWriter`, which is illegal. |
| Decoded request message, synchronous method | **Default: a normal GC object** whose `string` and `bytes` fields are materialized (copied). **Opt-in: a generated ref struct view** over the request span, with lazy field accessors (`ReadOnlySpan<byte> NameUtf8`, `string Name`) | The ref struct is the only compile-time-safe borrow. Copying small fields is cheaper than tracking references below about 512 B (Cornflakes). |
| Decoded request message, async/streaming | **GC object** (default) or **pooled object graph reset per call** (MemoryPack overwrite style) when the method opts in. Large `bytes` fields may be `ReadOnlyMemory<byte>` over **leased pages** valid until the method's task completes. | Mirrors MemoryPack overwrite and gRPC C++ `MessageAllocator`, with opt-in so default code stays safe. |
| Long-lived host I/O buffers | Kestrel's **POH block pool**. The core does not create its own pinned pools. | Kestrel already owns and evicts them. A second pinned pool adds fragmentation and retention risk (aspnetcore #55890). |
| Native memory | **Not used** in the first release | Use-after-free would be undefined behaviour and there is no GC accounting. Only `UnmanagedBufferAllocator`-style bump arenas for unmanaged scratch would be justified, and only if benchmarks show it. |
| `Task`/state machines | None on the synchronous path. `ValueTask` plus pooled `IValueTaskSource` only where suspension actually happens. | As in JSON-RPC.NET's "inline default rows allocate nothing" goal. |

**Unary fast path, end to end.**

1. The host resolves the path to a synchronous unary method.
2. It hands over a contiguous body, with one copy into per-thread scratch if the body arrived fragmented.
3. The core parses the 5-byte prefix in place and enforces the size limit.
4. It decodes, either into a ref struct view or into a GC object with copied fields.
5. It invokes the method, computes the response size, then writes prefix and payload directly into `responseBody`.
6. It returns `GrpcStatus` by value.

With a view-typed method and primitive fields, nothing reaches the GC heap. With a class-typed message, the only allocations are the message object and its strings, which is the user's explicit choice.

### 6.3 Ownership rules for methods receiving pooled-buffer-backed messages (inference)

1. **Views are borrowed for the call.** A ref struct request view, or any `ReadOnlySpan<byte>` accessor, is valid until the synchronous method returns, and the compiler enforces it. `ToString()`/`ToArray()`/`Clone()` produce owned copies.
2. **Class messages own their data by default.** A class-typed request parameter holds no references into pooled memory unless the method or the service opts in. The user may keep it indefinitely.
3. **Opt-in borrowed fields follow the Task lease.** When a method opts in, `ReadOnlyMemory<byte>` fields point into leased pages. They are valid until the method returns (synchronous) or its `Task`/`ValueTask` completes (async), matching guidelines Rules #3 and #4. The core releases the pages after the method's completion and after the response has been written.
4. **Streaming reads are valid until the next read.** A message produced by the typed stream reader is valid until the next `MoveNext`/`ReadAsync`, as `PipeReader` data is valid until `AdvanceTo`. Borrowed fields must be detached explicitly to keep them.
5. **Pooled message objects** (opt-in reuse) belong to the core. The method must not keep the message object itself after completion. Debug builds should poison or version-check it (see 6.4).
6. **Response writes copy synchronously.** `WriteAsync(message)` on a stream writer serializes into the pipe before it returns a pending task, so the caller may reuse or mutate the message as soon as the call returns. This is stronger than having to await completion, and it is what lets user code reuse one response object per stream.
7. **Exactly one owner releases a buffer**, per guidelines Rules #7 and #8. A host that passes the core a `ReadOnlyMemory<byte>` keeps ownership and must not reuse it until `ProcessAsync` completes, as JSON-RPC.NET's `ProcessAsync(ReadOnlyMemory<byte>)` documents today.

### 6.4 Risks and mitigations (inference, grounded in the cited failures)

- **Use after return.** This is the main risk of any borrowed-memory design. `ArrayPool` will not catch it (§1).
  - Prefer compile-time-scoped views (ref structs) for zero-copy.
  - For heap-storable borrowed slices, use Orleans-style page versions and tokens, checked on access in debug builds and optionally in release ([ArcBufferWriter.cs#L851-L901](https://github.com/dotnet/orleans/blob/1cc211b4c715e1a984d593a4689c9fde334251ea/src/Orleans.Serialization/Buffers/ArcBufferWriter.cs#L851-L901)).
  - Ship a test-mode pool, like Kestrel's `DiagnosticMemoryPool`, that poisons returned buffers and throws on late access.
- **Double return and cross-call data leakage.** Pooled and uninitialized arrays keep earlier requests' bytes (`AllocateUninitializedArray` in `SharedArrayPool.Rent`).
  - Never expose a span past its written length.
  - Never return a buffer twice; use a single owner struct with a `returned` flag.
  - Consider `clearArray: true` for buffers that held request metadata such as authorization headers.
- **Retention and fragmentation.**
  - Per-thread scratch multiplies by the number of threads. JSON-RPC.NET caps retained buffers at 64 KiB and drops oversized ones ([JsonRpcProcessor.Async.cs#L265-L277](https://github.com/Astn/JSON-RPC.NET/blob/a87c6b9bc0431e74c9d59b82e1835d8e84a0cce6/Json-Rpc/JsonRpcProcessor.Async.cs#L265-L277)), and GRPC.NET should do the same at its maximum message size.
  - POH pools need eviction (Kestrel's heartbeat eviction; #55890), and the POH is never compacted (PinnedHeap.md L89).
  - Avoid core-owned pinned memory unless measurements require it.
- **Pinning.**
  - Spans over managed arrays need no pinning in pure managed code.
  - Pinning is needed only for native I/O, which the host owns.
  - `fixed` across long operations, and `GCHandle` pins in ordinary heaps, cause the gen0 fragmentation the POH was created to avoid (PinnedHeap.md L19).
- **Unsafe code.**
  - `MemoryMarshal.Cast` and `Unsafe.ReadUnaligned` on wire data assume little-endian order (MemoryPack documents the same constraint).
  - They also need lengths validated before slicing. Any length from the wire must be checked against the remaining span and the configured message limit before `CreateSpan` or pointer use.
  - Keep `unsafe` confined to a small audited codec layer. Enable the default bounds-checked path in debug and fuzz tests.
- **Amplification and depth.**
  - Lazy views that re-traverse the same bytes are cheap in protobuf, which has no aliasing pointers unlike Cap'n Proto. Nested-message recursion still needs a depth limit, as in Cap'n Proto's nesting limit (64) and JSON-RPC.NET's `maxDepth` of 64.
  - The 5-byte prefix length must be checked against the maximum message size before any buffer is rented for it.
- **Async lease migration.** A lease taken on one thread may complete on another. JSON-RPC.NET's `AsyncScratch` returns to the completing thread's slot rather than the origin's, and its comment explains why (§0). Thread-static state must never be read after an `await` without re-renting.
- **API virality.** Go shelved arenas because arena parameters spread through APIs (#51317). Keep pooling inside the core and generated code, and expose at most an opt-in attribute or view type. Do not put an arena parameter on user method signatures.

### 6.5 What to measure before adopting any of this (inference)

1. **Allocation per call.** Use BenchmarkDotNet's allocation column for unary calls with 8-, 32-, 512- and 4096-byte messages, following ProtoAcc's size distribution. The view, GC-object and pooled-object variants should be compared within the same process, as JSON-RPC.NET's `BindingComparisonBenchmarks` does.
2. **Copy versus reference for `bytes` fields.** Sweep sizes to find this hardware's crossover; Cornflakes found 512 B on theirs.
3. **Gen0/gen1 counts and promoted bytes** under 256 in-flight calls per connection, the JSON-RPC.NET comparison setup. This shows whether class-typed messages survive into gen1 when calls overlap a GC.

### 6.6 Summary of verified facts versus inferences

- **Verified:** everything in §0 to §5 and §6.1 is taken directly from the cited source code, documentation or papers.
- **Inference:**
  - the recommendations in §6.2 to §6.5;
  - the claim that ref struct views are the only compile-time-safe zero-copy borrow in .NET;
  - the proposed size-first prefix strategy;
  - the "copy below about 512 B" policy carried over from Cornflakes' hardware;
  - the reading of library design choices noted as such in §2 and §4.
