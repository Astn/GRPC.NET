using System;

namespace GrpcNet
{
    /// <summary>
    /// Thrown by a service method to end the call with a specific status. A message is optional; without one the
    /// error path costs only the exception.
    /// </summary>
    public class GrpcException : Exception
    {
        /// <summary>Creates an exception that ends the call with <paramref name="code"/> and no message.</summary>
        public GrpcException(StatusCode code)
        {
            Code = code;
        }

        /// <summary>Creates an exception that ends the call with <paramref name="code"/> and <paramref name="message"/>.</summary>
        public GrpcException(StatusCode code, string? message)
            : base(message)
        {
            Code = code;
            HasStatusMessage = !string.IsNullOrEmpty(message);
        }

        /// <summary>The status code the call ends with.</summary>
        public StatusCode Code { get; }

        /// <summary>Whether the caller supplied a message to send as <c>grpc-message</c>.</summary>
        public bool HasStatusMessage { get; }
    }

    /// <summary>Thrown by a codec when a message cannot be decoded or encoded. The call ends with <see cref="StatusCode.Internal"/>.</summary>
    public sealed class GrpcCodecException : Exception
    {
        /// <summary>Creates a codec exception.</summary>
        public GrpcCodecException(string message)
            : base(message)
        {
        }

        /// <summary>Creates a codec exception that wraps the codec's own exception.</summary>
        public GrpcCodecException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
