using System;
using System.Buffers;

namespace GrpcNet
{
    /// <summary>Writes one framed response message: size first, then the prefix and the message in one contiguous span.</summary>
    internal static class ResponseWriter
    {
        internal static GrpcStatus Write<T, TCodec>(ref TCodec codec, in T message, IBufferWriter<byte> output, GrpcProcessorOptions options)
            where TCodec : struct, IMessageCodec<T>
        {
            int size;
            try
            {
                size = codec.Size(in message);
            }
            catch (Exception ex)
            {
                return StatusMapping.FromCodecException(ex, options);
            }

            if (size < 0)
            {
                return StatusMapping.InvalidCodecSize(options);
            }

            if (size > options.MaxSendMessageSize)
            {
                return new GrpcStatus(StatusCode.ResourceExhausted);
            }

            // MaxSendMessageSize is validated to be at most int.MaxValue - 5, so this cannot overflow.
            int framed = MessageFraming.PrefixLength + size;
            if (framed <= options.MaxContiguousResponseBytes)
            {
                Span<byte> span = output.GetSpan(framed);
                try
                {
                    codec.Encode(in message, size, span.Slice(MessageFraming.PrefixLength, size));
                }
                catch (Exception ex)
                {
                    // Nothing was advanced, so the writer holds no partial frame.
                    return StatusMapping.FromCodecException(ex, options);
                }

                MessageFraming.WritePrefix(span, size);
                output.Advance(framed);
                return GrpcStatus.Ok;
            }

            return WriteStaged(ref codec, in message, size, output, options);
        }

        private static GrpcStatus WriteStaged<T, TCodec>(ref TCodec codec, in T message, int size, IBufferWriter<byte> output, GrpcProcessorOptions options)
            where TCodec : struct, IMessageCodec<T>
        {
            byte[] staging = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                try
                {
                    codec.Encode(in message, size, staging.AsSpan(0, size));
                }
                catch (Exception ex)
                {
                    return StatusMapping.FromCodecException(ex, options);
                }

                Span<byte> prefix = output.GetSpan(MessageFraming.PrefixLength);
                MessageFraming.WritePrefix(prefix, size);
                output.Advance(MessageFraming.PrefixLength);

                ReadOnlySpan<byte> remaining = staging.AsSpan(0, size);
                while (!remaining.IsEmpty)
                {
                    Span<byte> destination = output.GetSpan();
                    int chunk = Math.Min(destination.Length, Math.Min(remaining.Length, options.MaxContiguousResponseBytes));
                    remaining.Slice(0, chunk).CopyTo(destination);
                    output.Advance(chunk);
                    remaining = remaining.Slice(chunk);
                }

                return GrpcStatus.Ok;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }
    }
}
