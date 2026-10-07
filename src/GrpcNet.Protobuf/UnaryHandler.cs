using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace GrpcNet.Protobuf
{
    /// <summary>Read by <see cref="GeneratedSupport.PreflightService"/> to check a binder member before anything is bound.</summary>
    internal interface IUnaryHandler
    {
        GrpcMethod? Origin { get; }
    }

    /// <summary>
    /// A unary handler for a generated binder, in one of its three forms: synchronous, asynchronous, or asynchronous with the
    /// call's cancellation token. Create it with <see cref="UnaryMethod{TRequest, TResponse}.Sync"/> or one of the
    /// <c>Async</c> overloads on the method it handles; the form is chosen once, when the binder binds, never per call.
    /// </summary>
    public readonly struct UnaryHandler<TRequest, TResponse> : IUnaryHandler
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        internal UnaryHandler(
            UnaryMethod<TRequest, TResponse> origin,
            Func<TRequest, TResponse>? sync,
            Func<TRequest, ValueTask<TResponse>>? tokenless,
            Func<TRequest, CancellationToken, ValueTask<TResponse>>? withToken)
        {
            Origin = origin;
            Sync = sync;
            Tokenless = tokenless;
            WithToken = withToken;
        }

        /// <summary>The method object this handler was made from; null for a default (empty) handler.</summary>
        internal UnaryMethod<TRequest, TResponse>? Origin { get; }

        internal Func<TRequest, TResponse>? Sync { get; }

        internal Func<TRequest, ValueTask<TResponse>>? Tokenless { get; }

        internal Func<TRequest, CancellationToken, ValueTask<TResponse>>? WithToken { get; }

        GrpcMethod? IUnaryHandler.Origin => Origin;
    }
}
