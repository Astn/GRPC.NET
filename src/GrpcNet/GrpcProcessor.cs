using System;
using System.Buffers;
using System.Collections.Generic;

namespace GrpcNet
{
    /// <summary>
    /// The transport-agnostic core. A host resolves the method, hands over the request body, and carries the framed response
    /// bytes and the status back out. <see cref="Process{TSink}(in CallHead, ReadOnlySpan{byte}, IBufferWriter{byte}, ref TSink)"/>
    /// is re-entrant, never flushes, never awaits, and allocates nothing beyond what the codec allocates for the messages.
    /// </summary>
    public sealed class GrpcProcessor
    {
        // Provisional: the per-thread scratch keeps at most this much between calls (JSON-RPC.NET's cap). CALL-INTERFACE.md
        // lists scratch retention as still open.
        internal const int RetainedScratchLimit = 64 * 1024;

        [ThreadStatic]
        private static byte[]? t_scratch;

        [ThreadStatic]
        private static bool t_scratchInUse;

        private readonly MethodTable _methods;
        private readonly GrpcProcessorOptions _options;

        internal GrpcProcessor(MethodTable methods, GrpcProcessorOptions options)
        {
            _methods = methods;
            _options = options;
        }

        /// <summary>Looks a method up by its path, such as <c>/package.Service/Method</c>, as UTF-8 bytes. Never allocates.</summary>
        public MethodId Resolve(ReadOnlySpan<byte> pathUtf8) => _methods.Resolve(pathUtf8);

        /// <summary>The kind of a resolved method, so the host can pick the unary path or the streaming path.</summary>
        public MethodKind GetKind(MethodId method)
        {
            MethodEntry entry = _methods.Get(method) ?? throw new ArgumentException("Unknown method.", nameof(method));
            return entry.Kind;
        }

        /// <summary>
        /// Runs a unary call whose complete request body (exactly one framed message) is <paramref name="body"/>, writes the
        /// framed response to <paramref name="output"/>, and completes <paramref name="sink"/>. Returns the call's status.
        /// </summary>
        public GrpcStatus Process<TSink>(in CallHead head, ReadOnlySpan<byte> body, IBufferWriter<byte> output, ref TSink sink)
            where TSink : struct, ICallSink
        {
            GrpcStatus status = ProcessSpan(in head, body, output);
            sink.Complete(in status, default);
            return status;
        }

        /// <summary>
        /// Runs a unary call whose complete request body may span segments. A single-segment body is decoded in place;
        /// otherwise the message is copied once into per-thread scratch.
        /// </summary>
        public GrpcStatus Process<TSink>(in CallHead head, in ReadOnlySequence<byte> body, IBufferWriter<byte> output, ref TSink sink)
            where TSink : struct, ICallSink
        {
            GrpcStatus status = body.IsSingleSegment ? ProcessSpan(in head, body.FirstSpan, output) : ProcessSegmented(in head, in body, output);
            sink.Complete(in status, default);
            return status;
        }

        private GrpcStatus ProcessSpan(in CallHead head, ReadOnlySpan<byte> body, IBufferWriter<byte> output)
        {
            MethodEntry? entry = _methods.Get(head.Method);
            if (entry == null)
            {
                return new GrpcStatus(StatusCode.Unimplemented);
            }

            if (!MessageFraming.TryReadPrefix(body, out bool compressed, out uint length))
            {
                return Malformed("The request body does not contain a message prefix.");
            }

            GrpcStatus rejected = CheckFrame(entry, in head, body[0], length, body.Length);
            if (rejected.Code != StatusCode.OK)
            {
                return rejected;
            }

            return entry.InvokeUnary(in head, body.Slice(MessageFraming.PrefixLength, (int)length), output, _options);
        }

        private GrpcStatus ProcessSegmented(in CallHead head, in ReadOnlySequence<byte> body, IBufferWriter<byte> output)
        {
            MethodEntry? entry = _methods.Get(head.Method);
            if (entry == null)
            {
                return new GrpcStatus(StatusCode.Unimplemented);
            }

            if (!MessageFraming.TryReadPrefix(in body, out bool compressed, out uint length))
            {
                return Malformed("The request body does not contain a message prefix.");
            }

            byte flag = body.FirstSpan.Length > 0 ? body.FirstSpan[0] : FirstByte(in body);
            GrpcStatus rejected = CheckFrame(entry, in head, flag, length, body.Length);
            if (rejected.Code != StatusCode.OK)
            {
                return rejected;
            }

            int size = (int)length;
            ReadOnlySequence<byte> payload = body.Slice(MessageFraming.PrefixLength, size);

            byte[]? rented = null;
            byte[] buffer;
            bool usingScratch = !t_scratchInUse && size <= RetainedScratchLimit;
            if (usingScratch)
            {
                buffer = t_scratch ??= new byte[RetainedScratchLimit];
                t_scratchInUse = true;
            }
            else
            {
                buffer = rented = ArrayPool<byte>.Shared.Rent(size);
            }

            try
            {
                payload.CopyTo(buffer);
                return entry.InvokeUnary(in head, buffer.AsSpan(0, size), output, _options);
            }
            finally
            {
                if (usingScratch)
                {
                    t_scratchInUse = false;
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(rented!);
                }
            }
        }

        private GrpcStatus CheckFrame(MethodEntry entry, in CallHead head, byte flag, uint length, long bodyLength)
        {
            if (entry.Kind != MethodKind.Unary)
            {
                return Malformed("The method is not unary; the host must use the streaming path.");
            }

            if (head.IsPastDeadline)
            {
                return new GrpcStatus(StatusCode.DeadlineExceeded);
            }

            if (flag > 1)
            {
                return Malformed("The message prefix has an invalid compressed flag.");
            }

            if (flag == 1)
            {
                return _options.DetailedErrors
                    ? GrpcStatus.FromString(StatusCode.Unimplemented, "Compressed messages are not supported.")
                    : new GrpcStatus(StatusCode.Unimplemented);
            }

            if (length > (uint)_options.MaxReceiveMessageSize)
            {
                return new GrpcStatus(StatusCode.ResourceExhausted);
            }

            long available = bodyLength - MessageFraming.PrefixLength;
            if (available < length)
            {
                return Malformed("The request body ends before the end of the message.");
            }

            if (available > length)
            {
                return Malformed("The request body contains data after the message.");
            }

            return GrpcStatus.Ok;
        }

        private GrpcStatus Malformed(string message)
        {
            return _options.DetailedErrors ? GrpcStatus.FromString(StatusCode.Internal, message) : new GrpcStatus(StatusCode.Internal);
        }

        private static byte FirstByte(in ReadOnlySequence<byte> body)
        {
            foreach (ReadOnlyMemory<byte> segment in body)
            {
                if (!segment.IsEmpty)
                {
                    return segment.Span[0];
                }
            }

            return 0;
        }
    }

    /// <summary>Builds a <see cref="GrpcProcessor"/>: registers methods, then freezes them into a method table.</summary>
    public sealed class GrpcProcessorBuilder
    {
        private readonly List<MethodEntry> _entries = new List<MethodEntry>();

        /// <summary>Limits and policies for the processor being built.</summary>
        public GrpcProcessorOptions Options { get; } = new GrpcProcessorOptions();

        /// <summary>
        /// Registers a unary method at <paramref name="path"/> (<c>/package.Service/Method</c>). This is the low-level
        /// registration that typed bindings build on.
        /// </summary>
        public GrpcProcessorBuilder AddUnary<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<TRequest, TResponse> handler)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            ValidatePath(path);
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _entries.Add(new UnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler));
            return this;
        }

        /// <summary>Validates the options, builds the method table and returns the processor. Throws when a path is bound twice.</summary>
        public GrpcProcessor Build()
        {
            GrpcProcessorOptions options = Options.Clone();
            options.Validate();
            return new GrpcProcessor(new MethodTable(_entries), options);
        }

        internal GrpcProcessor Build(MethodTable.PathHasher hasher)
        {
            GrpcProcessorOptions options = Options.Clone();
            options.Validate();
            return new GrpcProcessor(new MethodTable(_entries, hasher), options);
        }

        private static void ValidatePath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            int second = path.Length > 1 ? path.IndexOf('/', 1) : -1;
            if (path.Length < 4 || path[0] != '/' || second < 2 || second == path.Length - 1 || path.IndexOf('/', second + 1) >= 0)
            {
                throw new ArgumentException("A method path has the form /package.Service/Method: '" + path + "'.", nameof(path));
            }
        }
    }
}
