using System;
using System.Buffers;

namespace GrpcNet
{
    /// <summary>
    /// One method in the table. Each method is a sealed subclass closed over its message and codec types, so the single
    /// virtual call from the processor lands in code specialized for that method.
    /// </summary>
    internal abstract class MethodEntry
    {
        protected MethodEntry(string path, MethodKind kind)
        {
            Path = path;
            PathUtf8 = MethodTable.EncodePath(path);
            Kind = kind;
        }

        internal string Path { get; }

        internal byte[] PathUtf8 { get; }

        internal MethodKind Kind { get; }

        internal MethodId Id { get; set; }

        /// <summary>
        /// Decodes <paramref name="payload"/> (one complete message, already validated against the limits), runs the method
        /// and writes the framed response to <paramref name="output"/>. Returns the call's status.
        /// </summary>
        internal abstract GrpcStatus InvokeUnary(in CallHead head, ReadOnlySpan<byte> payload, IBufferWriter<byte> output, GrpcProcessorOptions options);
    }

    /// <summary>A unary method bound to a delegate.</summary>
    internal sealed class UnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec> : MethodEntry
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
    {
        private readonly Func<TRequest, TResponse> _handler;
        private TRequestCodec _requestCodec;
        private TResponseCodec _responseCodec;

        internal UnaryEntry(string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<TRequest, TResponse> handler)
            : base(path, MethodKind.Unary)
        {
            _requestCodec = requestCodec;
            _responseCodec = responseCodec;
            _handler = handler;
        }

        internal override GrpcStatus InvokeUnary(in CallHead head, ReadOnlySpan<byte> payload, IBufferWriter<byte> output, GrpcProcessorOptions options)
        {
            TRequest request;
            try
            {
                request = _requestCodec.Decode(payload);
            }
            catch (Exception ex)
            {
                return StatusMapping.FromCodecException(ex, options);
            }

            TResponse response;
            try
            {
                response = _handler(request);
            }
            catch (Exception ex)
            {
                return StatusMapping.FromException(ex, in head, options);
            }

            if (head.IsPastDeadline)
            {
                return new GrpcStatus(StatusCode.DeadlineExceeded);
            }

            return ResponseWriter.Write(ref _responseCodec, in response, output, options);
        }
    }
}
