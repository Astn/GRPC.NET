using System;

namespace GrpcNet
{
    /// <summary>Limits and policies for a <see cref="GrpcProcessor"/>. Read once when the processor is built.</summary>
    public sealed class GrpcProcessorOptions
    {
        /// <summary>The largest request message accepted, in bytes. Larger requests end with <see cref="StatusCode.ResourceExhausted"/>. Default 4 MiB.</summary>
        public int MaxReceiveMessageSize { get; set; } = 4 * 1024 * 1024;

        /// <summary>The largest response message sent, in bytes. Larger responses end with <see cref="StatusCode.ResourceExhausted"/>. Default <see cref="int.MaxValue"/> minus the prefix.</summary>
        public int MaxSendMessageSize { get; set; } = int.MaxValue - MessageFraming.PrefixLength;

        /// <summary>
        /// The largest framed response (prefix plus message) the host wants written as one contiguous span. Set by the host:
        /// above it the response is encoded into a staging buffer and copied out in chunks. Default: no cap, so the writer is
        /// always asked for one contiguous span.
        /// </summary>
        public int MaxContiguousResponseBytes { get; set; } = int.MaxValue;

        /// <summary>Whether the message of an unexpected exception is sent to the client in <c>grpc-message</c>. Default false.</summary>
        public bool DetailedErrors { get; set; }

        internal void Validate()
        {
            if (MaxReceiveMessageSize < 0) throw new ArgumentOutOfRangeException(nameof(MaxReceiveMessageSize));
            if (MaxSendMessageSize < 0 || MaxSendMessageSize > int.MaxValue - MessageFraming.PrefixLength) throw new ArgumentOutOfRangeException(nameof(MaxSendMessageSize));
            if (MaxContiguousResponseBytes < MessageFraming.PrefixLength) throw new ArgumentOutOfRangeException(nameof(MaxContiguousResponseBytes));
        }

        internal GrpcProcessorOptions Clone()
        {
            return (GrpcProcessorOptions)MemberwiseClone();
        }
    }
}
