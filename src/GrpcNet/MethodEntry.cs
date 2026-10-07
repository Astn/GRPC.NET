using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace GrpcNet
{
    /// <summary>
    /// One method in the table. Each method is a sealed subclass closed over its message and codec types, so the single
    /// virtual call from the processor lands in code specialized for that method.
    /// </summary>
    internal abstract class MethodEntry
    {
        protected MethodEntry(string path, MethodKind kind, Type requestType, Type responseType, object? contract)
        {
            Path = path;
            PathUtf8 = MethodTable.EncodePath(path);
            Kind = kind;
            Binding = new BoundMethod(path, kind, requestType, responseType, contract);
        }

        internal string Path { get; }

        internal byte[] PathUtf8 { get; }

        internal MethodKind Kind { get; }

        internal BoundMethod Binding { get; }

        internal MethodId Id { get; set; }

        /// <summary>Whether the method is unary with a synchronous handler, so a host with the whole body can use <c>Process</c>.</summary>
        internal virtual bool IsSynchronousUnary => false;

        /// <summary>
        /// Decodes <paramref name="payload"/> (one complete message, already validated against the limits), runs the method
        /// and writes the framed response to <paramref name="output"/>. Returns the call's status. Only for
        /// <see cref="IsSynchronousUnary"/> methods.
        /// </summary>
        internal virtual GrpcStatus InvokeUnary(in CallHead head, ReadOnlySpan<byte> payload, IBufferWriter<byte> output, GrpcProcessorOptions options)
            => throw new InvalidOperationException("The method is not synchronous unary.");

        /// <summary>Runs the call over the request and response pipes and returns its terminal status. Never throws.</summary>
        internal abstract ValueTask<GrpcStatus> InvokeAsync(CallHead head, PipeReader input, PipeWriter output, GrpcProcessorOptions options);
    }

    /// <summary>
    /// A method whose messages go through a pair of codecs. Holds the codecs as bound (each call works on its own copies, so a
    /// codec with mutable fields cannot leak them between calls) and runs every call kind over pipes.
    /// </summary>
    internal abstract class CodecEntry<TRequest, TResponse, TRequestCodec, TResponseCodec> : MethodEntry
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
    {
        private protected readonly TRequestCodec RequestCodec;
        private protected readonly TResponseCodec ResponseCodec;

        protected CodecEntry(string path, MethodKind kind, TRequestCodec requestCodec, TResponseCodec responseCodec, object? contract)
            : base(path, kind, typeof(TRequest), typeof(TResponse), contract)
        {
            RequestCodec = requestCodec;
            ResponseCodec = responseCodec;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        internal sealed override async ValueTask<GrpcStatus> InvokeAsync(CallHead head, PipeReader input, PipeWriter output, GrpcProcessorOptions options)
        {
            // The stream's reader and writer are reused when the host's state kept a matching pair; they are taken out of the
            // state while the call runs and put back when it ends.
            HostCallState? state = head.State;
            var reader = state?.ReaderCache as CodecReader<TRequest, TRequestCodec> ?? new CodecReader<TRequest, TRequestCodec>();
            var writer = state?.WriterCache as CodecWriter<TResponse, TResponseCodec> ?? new CodecWriter<TResponse, TResponseCodec>();
            if (state != null)
            {
                state.ReaderCache = null;
                state.WriterCache = null;
            }

            int readStamp = reader.Start(input, in head, options, RequestCodec);
            int writeStamp = writer.Start(output, in head, options, reader.Latch, ResponseCodec);

            GrpcStatus handled = GrpcStatus.Ok;
            try
            {
                await RunAsync(reader, readStamp, writer, writeStamp, head.Cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                handled = StatusMapping.FromException(ex, in head, options);
            }

            GrpcStatus status = Terminal(reader.Latch, in head, in handled);
            reader.End();
            writer.End();

            // A reader or writer still busy with an operation the handler never awaited is not reused.
            if (state != null && reader.IsIdle && writer.IsIdle)
            {
                state.ReaderCache = reader;
                state.WriterCache = writer;
            }

            return status;
        }

        /// <summary>Runs the handler. Unary and server-streaming calls read their single request here first.</summary>
        private protected abstract ValueTask RunAsync(
            ReaderCore<TRequest> reader, int readStamp, WriterCore<TResponse> writer, int writeStamp, CancellationToken cancellation);

        // The call's one outcome: a fault the core latched (protocol, deadline at a write, codec, host) wins, in the order it
        // happened; then a deadline or cancellation seen at the end; then what the handler did.
        private static GrpcStatus Terminal(CallLatch latch, in CallHead head, in GrpcStatus handled)
        {
            if (latch.HasFault)
            {
                return latch.Fault;
            }

            if (head.IsPastDeadline)
            {
                return new GrpcStatus(StatusCode.DeadlineExceeded);
            }

            if (head.Cancellation.IsCancellationRequested)
            {
                return new GrpcStatus(StatusCode.Cancelled);
            }

            return handled;
        }
    }

    /// <summary>A unary method with a synchronous invoker.</summary>
    internal sealed class UnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec, TInvoker> : CodecEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
        where TInvoker : struct, IUnaryInvoker<TRequest, TResponse>
    {
        private readonly TInvoker _invoker;

        internal UnaryEntry(string path, TRequestCodec requestCodec, TResponseCodec responseCodec, TInvoker invoker, object? contract)
            : base(path, MethodKind.Unary, requestCodec, responseCodec, contract)
        {
            _invoker = invoker;
        }

        internal override bool IsSynchronousUnary => true;

        internal override GrpcStatus InvokeUnary(in CallHead head, ReadOnlySpan<byte> payload, IBufferWriter<byte> output, GrpcProcessorOptions options)
        {
            TRequestCodec requestCodec = RequestCodec;
            TRequest request;
            try
            {
                request = requestCodec.Decode(payload);
            }
            catch (Exception ex)
            {
                return StatusMapping.FromCodecException(ex, options);
            }

            TResponse response;
            try
            {
                response = _invoker.Invoke(request);
            }
            catch (Exception ex)
            {
                return StatusMapping.FromException(ex, in head, options);
            }

            if (head.IsPastDeadline)
            {
                return new GrpcStatus(StatusCode.DeadlineExceeded);
            }

            TResponseCodec responseCodec = ResponseCodec;
            return ResponseWriter.Write(ref responseCodec, in response, output, options, out _);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private protected override async ValueTask RunAsync(
            ReaderCore<TRequest> reader, int readStamp, WriterCore<TResponse> writer, int writeStamp, CancellationToken cancellation)
        {
            TRequest request = await reader.ReadSingleAsync(readStamp).ConfigureAwait(false);
            TResponse response = _invoker.Invoke(request);
            await writer.WriteAsync(writeStamp, in response).ConfigureAwait(false);
        }
    }

    /// <summary>A unary method with an asynchronous invoker, with or without the cancellation token.</summary>
    internal sealed class AsyncUnaryEntry<TRequest, TResponse, TRequestCodec, TResponseCodec, TInvoker> : CodecEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
        where TInvoker : struct, IAsyncUnaryInvoker<TRequest, TResponse>
    {
        private readonly TInvoker _invoker;

        internal AsyncUnaryEntry(string path, TRequestCodec requestCodec, TResponseCodec responseCodec, TInvoker invoker, object? contract)
            : base(path, MethodKind.Unary, requestCodec, responseCodec, contract)
        {
            _invoker = invoker;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private protected override async ValueTask RunAsync(
            ReaderCore<TRequest> reader, int readStamp, WriterCore<TResponse> writer, int writeStamp, CancellationToken cancellation)
        {
            TRequest request = await reader.ReadSingleAsync(readStamp).ConfigureAwait(false);
            TResponse response = await _invoker.InvokeAsync(request, cancellation).ConfigureAwait(false);
            await writer.WriteAsync(writeStamp, in response).ConfigureAwait(false);
        }
    }

    /// <summary>A server-streaming method: one request, any number of responses.</summary>
    internal sealed class ServerStreamingEntry<TRequest, TResponse, TRequestCodec, TResponseCodec, TInvoker> : CodecEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
        where TInvoker : struct, IServerStreamingInvoker<TRequest, TResponse>
    {
        private readonly TInvoker _invoker;

        internal ServerStreamingEntry(string path, TRequestCodec requestCodec, TResponseCodec responseCodec, TInvoker invoker, object? contract)
            : base(path, MethodKind.ServerStreaming, requestCodec, responseCodec, contract)
        {
            _invoker = invoker;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private protected override async ValueTask RunAsync(
            ReaderCore<TRequest> reader, int readStamp, WriterCore<TResponse> writer, int writeStamp, CancellationToken cancellation)
        {
            TRequest request = await reader.ReadSingleAsync(readStamp).ConfigureAwait(false);
            await _invoker.InvokeAsync(request, new MessageWriter<TResponse>(writer, writeStamp), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>A client-streaming method: any number of requests, one response.</summary>
    internal sealed class ClientStreamingEntry<TRequest, TResponse, TRequestCodec, TResponseCodec, TInvoker> : CodecEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
        where TInvoker : struct, IClientStreamingInvoker<TRequest, TResponse>
    {
        private readonly TInvoker _invoker;

        internal ClientStreamingEntry(string path, TRequestCodec requestCodec, TResponseCodec responseCodec, TInvoker invoker, object? contract)
            : base(path, MethodKind.ClientStreaming, requestCodec, responseCodec, contract)
        {
            _invoker = invoker;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private protected override async ValueTask RunAsync(
            ReaderCore<TRequest> reader, int readStamp, WriterCore<TResponse> writer, int writeStamp, CancellationToken cancellation)
        {
            TResponse response = await _invoker.InvokeAsync(new MessageReader<TRequest>(reader, readStamp), cancellation).ConfigureAwait(false);
            await writer.WriteAsync(writeStamp, in response).ConfigureAwait(false);
        }
    }

    /// <summary>A bidirectional streaming method.</summary>
    internal sealed class DuplexEntry<TRequest, TResponse, TRequestCodec, TResponseCodec, TInvoker> : CodecEntry<TRequest, TResponse, TRequestCodec, TResponseCodec>
        where TRequestCodec : struct, IMessageCodec<TRequest>
        where TResponseCodec : struct, IMessageCodec<TResponse>
        where TInvoker : struct, IDuplexInvoker<TRequest, TResponse>
    {
        private readonly TInvoker _invoker;

        internal DuplexEntry(string path, TRequestCodec requestCodec, TResponseCodec responseCodec, TInvoker invoker, object? contract)
            : base(path, MethodKind.DuplexStreaming, requestCodec, responseCodec, contract)
        {
            _invoker = invoker;
        }

        private protected override ValueTask RunAsync(
            ReaderCore<TRequest> reader, int readStamp, WriterCore<TResponse> writer, int writeStamp, CancellationToken cancellation)
            => _invoker.InvokeAsync(new MessageReader<TRequest>(reader, readStamp), new MessageWriter<TResponse>(writer, writeStamp), cancellation);
    }
}
