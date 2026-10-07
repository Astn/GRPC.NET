using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace GrpcNet.Protobuf
{
    // Each method object holds the adder for its tag, chosen once when the object is created. Binding goes through it with one
    // virtual call at registration; every call after that runs in the entry closed over the tag's delegate invoker.

    /// <summary>The tag shared by every method object made with a public constructor rather than <c>Create</c>.</summary>
    internal struct UntaggedMethod
    {
    }

    internal abstract class UnaryAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        internal abstract void Sync(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, TResponse> handler);

        internal abstract void Tokenless(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, ValueTask<TResponse>> handler);

        internal abstract void WithToken(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler);
    }

    internal sealed class UnaryAdder<TRequest, TResponse, TTag> : UnaryAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
        where TTag : struct
    {
        internal static readonly UnaryAdder<TRequest, TResponse, TTag> Instance = new UnaryAdder<TRequest, TResponse, TTag>();

        internal override void Sync(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, TResponse> handler)
            => builder.AddUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, UnaryDelegate<TRequest, TResponse, TTag>>(
                method.Path, method.RequestCodec, method.ResponseCodec, new UnaryDelegate<TRequest, TResponse, TTag>(handler), method.Descriptor);

        internal override void Tokenless(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, ValueTask<TResponse>> handler)
            => builder.AddAsyncUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, TokenlessUnaryDelegate<TRequest, TResponse, TTag>>(
                method.Path, method.RequestCodec, method.ResponseCodec, new TokenlessUnaryDelegate<TRequest, TResponse, TTag>(handler), method.Descriptor);

        internal override void WithToken(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
            => builder.AddAsyncUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, AsyncUnaryDelegate<TRequest, TResponse, TTag>>(
                method.Path, method.RequestCodec, method.ResponseCodec, new AsyncUnaryDelegate<TRequest, TResponse, TTag>(handler), method.Descriptor);
    }

    internal abstract class ServerStreamingAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        internal abstract void Add(GrpcProcessorBuilder builder, ServerStreamingMethod<TRequest, TResponse> method, Func<TRequest, MessageWriter<TResponse>, CancellationToken, ValueTask> handler);
    }

    internal sealed class ServerStreamingAdder<TRequest, TResponse, TTag> : ServerStreamingAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
        where TTag : struct
    {
        internal static readonly ServerStreamingAdder<TRequest, TResponse, TTag> Instance = new ServerStreamingAdder<TRequest, TResponse, TTag>();

        internal override void Add(GrpcProcessorBuilder builder, ServerStreamingMethod<TRequest, TResponse> method, Func<TRequest, MessageWriter<TResponse>, CancellationToken, ValueTask> handler)
            => builder.AddServerStreaming<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, ServerStreamingDelegate<TRequest, TResponse, TTag>>(
                method.Path, method.RequestCodec, method.ResponseCodec, new ServerStreamingDelegate<TRequest, TResponse, TTag>(handler), method.Descriptor);
    }

    internal abstract class ClientStreamingAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        internal abstract void Add(GrpcProcessorBuilder builder, ClientStreamingMethod<TRequest, TResponse> method, Func<MessageReader<TRequest>, CancellationToken, ValueTask<TResponse>> handler);
    }

    internal sealed class ClientStreamingAdder<TRequest, TResponse, TTag> : ClientStreamingAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
        where TTag : struct
    {
        internal static readonly ClientStreamingAdder<TRequest, TResponse, TTag> Instance = new ClientStreamingAdder<TRequest, TResponse, TTag>();

        internal override void Add(GrpcProcessorBuilder builder, ClientStreamingMethod<TRequest, TResponse> method, Func<MessageReader<TRequest>, CancellationToken, ValueTask<TResponse>> handler)
            => builder.AddClientStreaming<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, ClientStreamingDelegate<TRequest, TResponse, TTag>>(
                method.Path, method.RequestCodec, method.ResponseCodec, new ClientStreamingDelegate<TRequest, TResponse, TTag>(handler), method.Descriptor);
    }

    internal abstract class DuplexAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        internal abstract void Add(GrpcProcessorBuilder builder, DuplexMethod<TRequest, TResponse> method, Func<MessageReader<TRequest>, MessageWriter<TResponse>, CancellationToken, ValueTask> handler);
    }

    internal sealed class DuplexAdder<TRequest, TResponse, TTag> : DuplexAdder<TRequest, TResponse>
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
        where TTag : struct
    {
        internal static readonly DuplexAdder<TRequest, TResponse, TTag> Instance = new DuplexAdder<TRequest, TResponse, TTag>();

        internal override void Add(GrpcProcessorBuilder builder, DuplexMethod<TRequest, TResponse> method, Func<MessageReader<TRequest>, MessageWriter<TResponse>, CancellationToken, ValueTask> handler)
            => builder.AddDuplex<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, DuplexDelegate<TRequest, TResponse, TTag>>(
                method.Path, method.RequestCodec, method.ResponseCodec, new DuplexDelegate<TRequest, TResponse, TTag>(handler), method.Descriptor);
    }
}
