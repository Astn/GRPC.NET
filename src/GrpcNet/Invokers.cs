using System;
using System.Threading;
using System.Threading.Tasks;

namespace GrpcNet
{
    /// <summary>
    /// Runs a synchronous unary method. An entry holds its invoker as a struct and calls it directly, so a distinct invoker
    /// type per method gives each method its own compiled call.
    /// </summary>
    public interface IUnaryInvoker<TRequest, TResponse>
    {
        /// <summary>Handles one request.</summary>
        TResponse Invoke(TRequest request);
    }

    /// <summary>Runs an asynchronous unary method. See <see cref="IUnaryInvoker{TRequest, TResponse}"/>.</summary>
    public interface IAsyncUnaryInvoker<TRequest, TResponse>
    {
        /// <summary>Handles one request.</summary>
        ValueTask<TResponse> InvokeAsync(TRequest request, CancellationToken cancellation);
    }

    /// <summary>Runs a server-streaming method. See <see cref="IUnaryInvoker{TRequest, TResponse}"/>.</summary>
    public interface IServerStreamingInvoker<TRequest, TResponse>
    {
        /// <summary>Handles one request, writing any number of responses.</summary>
        ValueTask InvokeAsync(TRequest request, MessageWriter<TResponse> responses, CancellationToken cancellation);
    }

    /// <summary>Runs a client-streaming method. See <see cref="IUnaryInvoker{TRequest, TResponse}"/>.</summary>
    public interface IClientStreamingInvoker<TRequest, TResponse>
    {
        /// <summary>Reads any number of requests and returns one response.</summary>
        ValueTask<TResponse> InvokeAsync(MessageReader<TRequest> requests, CancellationToken cancellation);
    }

    /// <summary>Runs a bidirectional streaming method. See <see cref="IUnaryInvoker{TRequest, TResponse}"/>.</summary>
    public interface IDuplexInvoker<TRequest, TResponse>
    {
        /// <summary>Reads requests and writes responses.</summary>
        ValueTask InvokeAsync(MessageReader<TRequest> requests, MessageWriter<TResponse> responses, CancellationToken cancellation);
    }

    // The delegate invokers. TTag is any struct: a value-type argument is never shared between instantiations, so a distinct
    // tag per method gives each method its own compiled entry and its own delegate call site, which dynamic PGO profiles
    // per method. One tag for every method gives the shared code a plain delegate field would.

    /// <summary>A synchronous unary handler delegate as an invoker. <typeparamref name="TTag"/> separates the compiled code per method.</summary>
    public readonly struct UnaryDelegate<TRequest, TResponse, TTag> : IUnaryInvoker<TRequest, TResponse>
        where TTag : struct
    {
        private readonly Func<TRequest, TResponse> _handler;

        /// <summary>Wraps <paramref name="handler"/>.</summary>
        public UnaryDelegate(Func<TRequest, TResponse> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <inheritdoc />
        public TResponse Invoke(TRequest request) => _handler(request);
    }

    /// <summary>An asynchronous unary handler delegate that takes the call's cancellation token, as an invoker.</summary>
    public readonly struct AsyncUnaryDelegate<TRequest, TResponse, TTag> : IAsyncUnaryInvoker<TRequest, TResponse>
        where TTag : struct
    {
        private readonly Func<TRequest, CancellationToken, ValueTask<TResponse>> _handler;

        /// <summary>Wraps <paramref name="handler"/>.</summary>
        public AsyncUnaryDelegate(Func<TRequest, CancellationToken, ValueTask<TResponse>> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <inheritdoc />
        public ValueTask<TResponse> InvokeAsync(TRequest request, CancellationToken cancellation) => _handler(request, cancellation);
    }

    /// <summary>
    /// An asynchronous unary handler delegate without a cancellation token, as an invoker. The call's deadline and
    /// cancellation are still observed when the handler completes; they cannot interrupt it.
    /// </summary>
    public readonly struct TokenlessUnaryDelegate<TRequest, TResponse, TTag> : IAsyncUnaryInvoker<TRequest, TResponse>
        where TTag : struct
    {
        private readonly Func<TRequest, ValueTask<TResponse>> _handler;

        /// <summary>Wraps <paramref name="handler"/>.</summary>
        public TokenlessUnaryDelegate(Func<TRequest, ValueTask<TResponse>> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <inheritdoc />
        public ValueTask<TResponse> InvokeAsync(TRequest request, CancellationToken cancellation) => _handler(request);
    }

    /// <summary>A server-streaming handler delegate as an invoker.</summary>
    public readonly struct ServerStreamingDelegate<TRequest, TResponse, TTag> : IServerStreamingInvoker<TRequest, TResponse>
        where TTag : struct
    {
        private readonly Func<TRequest, MessageWriter<TResponse>, CancellationToken, ValueTask> _handler;

        /// <summary>Wraps <paramref name="handler"/>.</summary>
        public ServerStreamingDelegate(Func<TRequest, MessageWriter<TResponse>, CancellationToken, ValueTask> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <inheritdoc />
        public ValueTask InvokeAsync(TRequest request, MessageWriter<TResponse> responses, CancellationToken cancellation) => _handler(request, responses, cancellation);
    }

    /// <summary>A client-streaming handler delegate as an invoker.</summary>
    public readonly struct ClientStreamingDelegate<TRequest, TResponse, TTag> : IClientStreamingInvoker<TRequest, TResponse>
        where TTag : struct
    {
        private readonly Func<MessageReader<TRequest>, CancellationToken, ValueTask<TResponse>> _handler;

        /// <summary>Wraps <paramref name="handler"/>.</summary>
        public ClientStreamingDelegate(Func<MessageReader<TRequest>, CancellationToken, ValueTask<TResponse>> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <inheritdoc />
        public ValueTask<TResponse> InvokeAsync(MessageReader<TRequest> requests, CancellationToken cancellation) => _handler(requests, cancellation);
    }

    /// <summary>A bidirectional streaming handler delegate as an invoker.</summary>
    public readonly struct DuplexDelegate<TRequest, TResponse, TTag> : IDuplexInvoker<TRequest, TResponse>
        where TTag : struct
    {
        private readonly Func<MessageReader<TRequest>, MessageWriter<TResponse>, CancellationToken, ValueTask> _handler;

        /// <summary>Wraps <paramref name="handler"/>.</summary>
        public DuplexDelegate(Func<MessageReader<TRequest>, MessageWriter<TResponse>, CancellationToken, ValueTask> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        /// <inheritdoc />
        public ValueTask InvokeAsync(MessageReader<TRequest> requests, MessageWriter<TResponse> responses, CancellationToken cancellation) => _handler(requests, responses, cancellation);
    }

    /// <summary>The one tag every delegate bound without a tag shares.</summary>
    internal struct SharedTag
    {
    }
}
