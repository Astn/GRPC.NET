using System;
using System.Buffers;
using System.Buffers.Binary;

namespace GrpcNet
{
    /// <summary>
    /// gRPC's own message framing: a 1-byte compressed flag followed by a 4-byte big-endian length, then the message.
    /// </summary>
    public static class MessageFraming
    {
        /// <summary>The size of the prefix in bytes.</summary>
        public const int PrefixLength = 5;

        /// <summary>Reads a prefix from the start of <paramref name="source"/>. Returns false when fewer than 5 bytes are present.</summary>
        public static bool TryReadPrefix(ReadOnlySpan<byte> source, out bool compressed, out uint length)
        {
            if (source.Length < PrefixLength)
            {
                compressed = false;
                length = 0;
                return false;
            }

            compressed = source[0] != 0;
            length = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(1));
            return true;
        }

        /// <summary>
        /// Reads a prefix from the start of <paramref name="source"/>. The bytes are read in place; they are copied to the
        /// stack only when the prefix straddles segments. Returns false when fewer than 5 bytes are present.
        /// </summary>
        public static bool TryReadPrefix(in ReadOnlySequence<byte> source, out bool compressed, out uint length)
        {
            ReadOnlySpan<byte> first = source.FirstSpan;
            if (first.Length >= PrefixLength)
            {
                return TryReadPrefix(first, out compressed, out length);
            }

            if (source.Length < PrefixLength)
            {
                compressed = false;
                length = 0;
                return false;
            }

            Span<byte> prefix = stackalloc byte[PrefixLength];
            source.Slice(0, PrefixLength).CopyTo(prefix);
            return TryReadPrefix(prefix, out compressed, out length);
        }

        /// <summary>Writes an uncompressed prefix for a message of <paramref name="length"/> bytes.</summary>
        public static void WritePrefix(Span<byte> destination, int length)
        {
            destination[0] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(1), (uint)length);
        }
    }
}
