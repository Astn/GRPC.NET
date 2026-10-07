using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace GrpcNet
{
    /// <summary>
    /// The transport-agnostic core. A host resolves the method, hands over the request body, and carries the framed response
    /// bytes and the status back out. Processing is re-entrant, never flushes and never awaits. A contiguous request body with
    /// a response written as one span allocates nothing beyond what the codec allocates for the messages. A segmented body
    /// is copied into per-thread scratch (allocated once per thread) or, when that is busy or too small, a pooled buffer; a
    /// response above the host's contiguous cap is staged in a pooled buffer.
    /// </summary>
    public sealed class GrpcProcessor
    {
        internal const int RetainedScratchLimit = PayloadScratch.RetainedLimit;

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
        /// Whether the method is unary with a synchronous handler. A host that has the whole request body runs such a method
        /// with <c>Process</c>; every other method runs with <c>ProcessAsync</c>.
        /// </summary>
        public bool IsSynchronousUnary(MethodId method) => _methods.Get(method)?.IsSynchronousUnary ?? false;

        /// <summary>
        /// Runs any call over the request and response pipes: streaming calls, asynchronous methods, and unary calls whose
        /// body has not fully arrived. Completes <paramref name="sink"/> exactly once, with the call's one terminal status.
        /// The core never flushes <paramref name="responseBody"/>; after each response message it calls the host's
        /// <see cref="HostCallState.OnMessageWritten"/>. The host owns both pipes and completes them after the call.
        /// </summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        public async ValueTask ProcessAsync<TSink>(CallHead head, PipeReader requestBody, PipeWriter responseBody, TSink sink)
            where TSink : struct, ICallSink
        {
            if (requestBody == null) throw new ArgumentNullException(nameof(requestBody));
            if (responseBody == null) throw new ArgumentNullException(nameof(responseBody));

            GrpcStatus status;
            MethodEntry? entry = _methods.Get(head.Method);
            if (entry == null)
            {
                status = new GrpcStatus(StatusCode.Unimplemented);
            }
            else if (head.IsPastDeadline)
            {
                status = new GrpcStatus(StatusCode.DeadlineExceeded);
            }
            else
            {
                status = await entry.InvokeAsync(head, requestBody, responseBody, _options).ConfigureAwait(false);
            }

            sink.Complete(in status, default);
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

            GrpcStatus rejected = CheckCall(entry, in head);
            if (rejected.Code != StatusCode.OK)
            {
                return rejected;
            }

            if (!MessageFraming.TryReadPrefix(body, out _, out uint length))
            {
                return Malformed("The request body does not contain a message prefix.");
            }

            rejected = CheckFrame(body[0], length, body.Length);
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

            GrpcStatus rejected = CheckCall(entry, in head);
            if (rejected.Code != StatusCode.OK)
            {
                return rejected;
            }

            if (!MessageFraming.TryReadPrefix(in body, out _, out uint length))
            {
                return Malformed("The request body does not contain a message prefix.");
            }

            byte flag = body.FirstSpan.Length > 0 ? body.FirstSpan[0] : FirstByte(in body);
            rejected = CheckFrame(flag, length, body.Length);
            if (rejected.Code != StatusCode.OK)
            {
                return rejected;
            }

            int size = (int)length;
            ReadOnlySequence<byte> payload = body.Slice(MessageFraming.PrefixLength, size);

            byte[] buffer = PayloadScratch.Acquire(size, out bool rented);
            try
            {
                payload.CopyTo(buffer);
                return entry.InvokeUnary(in head, buffer.AsSpan(0, size), output, _options);
            }
            finally
            {
                PayloadScratch.Release(buffer, rented);
            }
        }

        // The call is checked before its body, so an expired call ends with DEADLINE_EXCEEDED whatever the body holds.
        private GrpcStatus CheckCall(MethodEntry entry, in CallHead head)
        {
            if (!entry.IsSynchronousUnary)
            {
                return Malformed("The method is not synchronous unary; the host must use ProcessAsync.");
            }

            return head.IsPastDeadline ? new GrpcStatus(StatusCode.DeadlineExceeded) : GrpcStatus.Ok;
        }

        private GrpcStatus CheckFrame(byte flag, uint length, long bodyLength)
        {
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
        private readonly HashSet<string> _paths = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<BoundMethod> _bindings = new List<BoundMethod>();
        private readonly ReadOnlyCollection<BoundMethod> _bindingsView;
        private readonly List<Action<IReadOnlyList<BoundMethod>, ICollection<string>>> _checks = new List<Action<IReadOnlyList<BoundMethod>, ICollection<string>>>();

        /// <summary>Creates an empty builder.</summary>
        public GrpcProcessorBuilder()
        {
            // Callers and checks see a read-only view, so nothing can hide a binding from the checks.
            _bindingsView = _bindings.AsReadOnly();
        }

        /// <summary>Limits and policies for the processor being built.</summary>
        public GrpcProcessorOptions Options { get; } = new GrpcProcessorOptions();

        /// <summary>The methods registered so far, in registration order.</summary>
        public IReadOnlyList<BoundMethod> Bindings => _bindingsView;

        /// <summary>
        /// Registers a unary method at <paramref name="path"/> (<c>/package.Service/Method</c>). This is the low-level
        /// registration that typed bindings build on. <paramref name="contract"/> is what the binding claims to implement,
        /// kept for the registered checks only. Throws when the path is already bound.
        /// </summary>
        public GrpcProcessorBuilder AddUnary<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<TRequest, TResponse> handler, object? contract = null)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            Reserve(path, handler);
            return Add(new UnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler, contract));
        }

        /// <summary>Registers a unary method with an asynchronous handler. See <see cref="AddUnary{TRequest, TResponse, TRequestCodec, TResponseCodec}"/>.</summary>
        public GrpcProcessorBuilder AddAsyncUnary<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler, object? contract = null)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            Reserve(path, handler);
            return Add(new AsyncUnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler, contract));
        }

        /// <summary>
        /// Registers an asynchronous unary method whose handler takes no cancellation token. The call's deadline and
        /// cancellation are still observed when the handler completes; they cannot interrupt it.
        /// </summary>
        public GrpcProcessorBuilder AddAsyncUnary<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<TRequest, ValueTask<TResponse>> handler, object? contract = null)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            Reserve(path, handler);
            return Add(new TokenlessUnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler, contract));
        }

        /// <summary>Registers a server-streaming method. See <see cref="AddUnary{TRequest, TResponse, TRequestCodec, TResponseCodec}"/>.</summary>
        public GrpcProcessorBuilder AddServerStreaming<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<TRequest, MessageWriter<TResponse>, CancellationToken, ValueTask> handler, object? contract = null)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            Reserve(path, handler);
            return Add(new ServerStreamingEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler, contract));
        }

        /// <summary>Registers a client-streaming method. See <see cref="AddUnary{TRequest, TResponse, TRequestCodec, TResponseCodec}"/>.</summary>
        public GrpcProcessorBuilder AddClientStreaming<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<MessageReader<TRequest>, CancellationToken, ValueTask<TResponse>> handler, object? contract = null)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            Reserve(path, handler);
            return Add(new ClientStreamingEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler, contract));
        }

        /// <summary>Registers a bidirectional streaming method. See <see cref="AddUnary{TRequest, TResponse, TRequestCodec, TResponseCodec}"/>.</summary>
        public GrpcProcessorBuilder AddDuplex<TRequest, TResponse, TRequestCodec, TResponseCodec>(
            string path, TRequestCodec requestCodec, TResponseCodec responseCodec, Func<MessageReader<TRequest>, MessageWriter<TResponse>, CancellationToken, ValueTask> handler, object? contract = null)
            where TRequestCodec : struct, IMessageCodec<TRequest>
            where TResponseCodec : struct, IMessageCodec<TResponse>
        {
            Reserve(path, handler);
            return Add(new DuplexEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>(path, requestCodec, responseCodec, handler, contract));
        }

        // Validates a registration before anything changes, so a rejected one leaves the builder as it was.
        private void Reserve(string path, Delegate handler)
        {
            ValidatePath(path);
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_paths.Add(path))
            {
                throw new InvalidOperationException("The method '" + path + "' is bound more than once.");
            }
        }

        private GrpcProcessorBuilder Add(MethodEntry entry)
        {
            _entries.Add(entry);
            _bindings.Add(entry.Binding);
            return this;
        }

        /// <summary>
        /// Adds a check that <see cref="Check"/> and <see cref="Build()"/> run over every registered method. A check reports
        /// each problem it finds by adding a message; it must not change the builder.
        /// </summary>
        public GrpcProcessorBuilder AddCheck(Action<IReadOnlyList<BoundMethod>, ICollection<string>> check)
        {
            _checks.Add(check ?? throw new ArgumentNullException(nameof(check)));
            return this;
        }

        /// <summary>Runs every registered check and returns all the problems found, without building. Empty when all pass.</summary>
        public IReadOnlyList<string> Check()
        {
            var problems = new List<string>();
            foreach (Action<IReadOnlyList<BoundMethod>, ICollection<string>> check in _checks)
            {
                check(_bindingsView, problems);
            }

            return problems;
        }

        /// <summary>
        /// Runs the checks, validates the options, builds the method table and returns the processor. Throws, listing every
        /// problem, when a check fails.
        /// </summary>
        public GrpcProcessor Build() => Build(null);

        internal GrpcProcessor Build(MethodTable.PathHasher? hasher)
        {
            IReadOnlyList<string> problems = Check();
            if (problems.Count > 0)
            {
                string separator = Environment.NewLine + "  ";
                throw new InvalidOperationException("The bound methods do not match their contracts:" + separator + string.Join(separator, problems));
            }

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
