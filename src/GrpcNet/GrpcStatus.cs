using System;
using System.Text;

namespace GrpcNet
{
    /// <summary>
    /// The outcome of a call: a status code and an optional message. The message is UTF-8 and is empty for
    /// <see cref="StatusCode.OK"/>; the host percent-encodes it into <c>grpc-message</c>.
    /// </summary>
    public readonly struct GrpcStatus
    {
        /// <summary>The status of a successful call.</summary>
        public static GrpcStatus Ok => default;

        /// <summary>Creates a status with a UTF-8 message.</summary>
        public GrpcStatus(StatusCode code, ReadOnlyMemory<byte> message)
        {
            Code = code;
            Message = message;
        }

        /// <summary>Creates a status without a message.</summary>
        public GrpcStatus(StatusCode code)
        {
            Code = code;
            Message = default;
        }

        /// <summary>The status code.</summary>
        public StatusCode Code { get; }

        /// <summary>The status message as UTF-8 bytes; empty when there is none.</summary>
        public ReadOnlyMemory<byte> Message { get; }

        /// <summary>Creates a status whose message is encoded from <paramref name="message"/>. Allocates; meant for error paths.</summary>
        public static GrpcStatus FromString(StatusCode code, string? message)
        {
            return string.IsNullOrEmpty(message) ? new GrpcStatus(code) : new GrpcStatus(code, Encoding.UTF8.GetBytes(message));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Message.IsEmpty ? Code.ToString() : Code + ": " + Encoding.UTF8.GetString(Message.Span);
        }
    }
}
