using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GrpcNet.Tests
{
    /// <summary>A 4-byte little-endian integer message, so the core can be tested without a protobuf dependency.</summary>
    internal struct IntCodec : IMessageCodec<int>
    {
        public int Decode(ReadOnlySpan<byte> payload)
        {
            if (payload.Length != 4) throw new GrpcCodecException("An int message is 4 bytes.");
            return BinaryPrimitives.ReadInt32LittleEndian(payload);
        }

        public int Size(in int message) => 4;

        public void Encode(in int message, int size, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, message);
        }
    }

    /// <summary>A message that is its raw bytes.</summary>
    internal struct BytesCodec : IMessageCodec<byte[]>
    {
        public byte[] Decode(ReadOnlySpan<byte> payload) => payload.ToArray();

        public int Size(in byte[] message) => message.Length;

        public void Encode(in byte[] message, int size, Span<byte> destination) => message.AsSpan().CopyTo(destination);
    }

    public enum Fault
    {
        None,
        DecodeThrows,
        SizeThrows,
        SizeNegative,
        EncodeThrows,
    }

    /// <summary>A bytes codec that fails in a chosen place.</summary>
    internal struct FaultyCodec : IMessageCodec<byte[]>
    {
        private readonly Fault _fault;

        public FaultyCodec(Fault fault)
        {
            _fault = fault;
        }

        public byte[] Decode(ReadOnlySpan<byte> payload)
        {
            if (_fault == Fault.DecodeThrows) throw new GrpcCodecException("decode failed");
            return payload.ToArray();
        }

        public int Size(in byte[] message)
        {
            if (_fault == Fault.SizeThrows) throw new InvalidOperationException("size failed");
            return _fault == Fault.SizeNegative ? -1 : message.Length;
        }

        public void Encode(in byte[] message, int size, Span<byte> destination)
        {
            // Write some bytes first, so a test can see that they were never committed.
            message.AsSpan(0, Math.Min(message.Length, destination.Length)).CopyTo(destination);
            if (_fault == Fault.EncodeThrows) throw new InvalidOperationException("encode failed");
        }
    }

    /// <summary>Records sink events.</summary>
    internal struct RecordingSink : ICallSink
    {
        public int CompleteCount;
        public int HeadersCount;
        public GrpcStatus LastStatus;

        public void WriteHeaders(in MetadataView headers) => HeadersCount++;

        public void Complete(in GrpcStatus status, in MetadataView trailers)
        {
            CompleteCount++;
            LastStatus = status;
        }
    }

    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> that honours the contract (a span of at least sizeHint) but hands out small spans
    /// when no size is requested, to exercise chunked copies.
    /// </summary>
    internal sealed class SmallSpanWriter : IBufferWriter<byte>
    {
        private readonly int _chunk;
        private readonly List<byte> _written = new List<byte>();
        private byte[] _current = Array.Empty<byte>();

        public SmallSpanWriter(int chunk)
        {
            _chunk = chunk;
        }

        public int GetSpanCalls { get; private set; }

        public byte[] Written => _written.ToArray();

        public void Advance(int count)
        {
            if (count < 0 || count > _current.Length) throw new InvalidOperationException("Advanced past the span.");
            _written.AddRange(_current.AsSpan(0, count).ToArray());
            _current = Array.Empty<byte>();
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            GetSpanCalls++;
            _current = new byte[Math.Max(sizeHint, _chunk)];
            return _current;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    internal static class Frames
    {
        public static byte[] Frame(ReadOnlySpan<byte> payload, byte flag = 0)
        {
            var frame = new byte[MessageFraming.PrefixLength + payload.Length];
            MessageFraming.WritePrefix(frame, payload.Length);
            frame[0] = flag;
            payload.CopyTo(frame.AsSpan(MessageFraming.PrefixLength));
            return frame;
        }

        public static byte[] IntFrame(int value)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload, value);
            return Frame(payload);
        }

        /// <summary>Splits <paramref name="data"/> into a sequence whose segments break at each of <paramref name="splits"/>.</summary>
        public static ReadOnlySequence<byte> Split(byte[] data, params int[] splits)
        {
            var bounds = new List<int> { 0 };
            foreach (int s in splits)
            {
                if (s > bounds[bounds.Count - 1] && s < data.Length) bounds.Add(s);
            }

            bounds.Add(data.Length);
            Segment? first = null;
            Segment? last = null;
            for (int i = 0; i < bounds.Count - 1; i++)
            {
                var memory = new ReadOnlyMemory<byte>(data, bounds[i], bounds[i + 1] - bounds[i]);
                if (first == null)
                {
                    first = last = new Segment(memory, 0);
                }
                else
                {
                    last = last!.Append(memory);
                }
            }

            if (first == null)
            {
                return ReadOnlySequence<byte>.Empty;
            }

            return new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
        }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
            {
                Memory = memory;
                RunningIndex = runningIndex;
            }

            public Segment Append(ReadOnlyMemory<byte> memory)
            {
                var next = new Segment(memory, RunningIndex + Memory.Length);
                Next = next;
                return next;
            }
        }
    }
}
