using System;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GrpcNet.Protobuf
{
    /// <summary>
    /// One method of a <c>.proto</c> service, resolved by name and checked against the descriptor when it is created. The
    /// generator emits one static instance per method; its type carries the request type, the response type and the call kind,
    /// so <c>Bind</c> infers the handler's types from it.
    /// </summary>
    public abstract class GrpcMethod
    {
        private protected GrpcMethod(FileDescriptor file, string serviceFullName, string methodName, MethodKind kind, Type requestType, Type responseType)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (serviceFullName == null) throw new ArgumentNullException(nameof(serviceFullName));
            if (methodName == null) throw new ArgumentNullException(nameof(methodName));

            string fullName = serviceFullName + "." + methodName;
            ServiceDescriptor? service = null;
            foreach (ServiceDescriptor candidate in file.Services)
            {
                if (candidate.FullName == serviceFullName)
                {
                    service = candidate;
                    break;
                }
            }

            if (service == null)
            {
                throw new InvalidOperationException("The service '" + serviceFullName + "' is not declared in '" + file.Name + "'. The generated code is out of date with its .proto.");
            }

            MethodDescriptor? method = service.FindMethodByName(methodName);
            if (method == null)
            {
                throw new InvalidOperationException("The method '" + fullName + "' is not declared in '" + file.Name + "'. The generated code is out of date with its .proto.");
            }

            string? mismatch = ProtobufContract.Mismatch(method, kind, requestType, responseType);
            if (mismatch != null)
            {
                throw new InvalidOperationException("The method object for '" + fullName + "' " + mismatch + ". The generated code is out of date with its .proto.");
            }

            Descriptor = method;
            Kind = kind;
            Path = "/" + serviceFullName + "/" + methodName;
            PathUtf8 = Encoding.UTF8.GetBytes(Path);
        }

        /// <summary>The method's descriptor.</summary>
        public MethodDescriptor Descriptor { get; }

        /// <summary>The method's call kind.</summary>
        public MethodKind Kind { get; }

        /// <summary>The method's path, <c>/package.Service/Method</c>.</summary>
        public string Path { get; }

        /// <summary>The method's path as UTF-8 bytes, for resolving a method without a string.</summary>
        public ReadOnlyMemory<byte> PathUtf8 { get; }

        /// <inheritdoc />
        public override string ToString() => Path;

        private protected static ProtobufCodec<T> CodecFor<T>(MessageDescriptor descriptor)
            where T : class, IMessage<T>
        {
            // The descriptor's parser is the generated type's static Parser; its CLR type was checked above.
            return new ProtobufCodec<T>((MessageParser<T>)descriptor.Parser);
        }
    }

    /// <summary>A unary method: one request, one response.</summary>
    public sealed class UnaryMethod<TRequest, TResponse> : GrpcMethod
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        /// <summary>Resolves <paramref name="serviceFullName"/>/<paramref name="methodName"/> in <paramref name="file"/> and checks its types and kind.</summary>
        public UnaryMethod(FileDescriptor file, string serviceFullName, string methodName)
            : base(file, serviceFullName, methodName, MethodKind.Unary, typeof(TRequest), typeof(TResponse))
        {
            RequestCodec = CodecFor<TRequest>(Descriptor.InputType);
            ResponseCodec = CodecFor<TResponse>(Descriptor.OutputType);
        }

        internal ProtobufCodec<TRequest> RequestCodec { get; }

        internal ProtobufCodec<TResponse> ResponseCodec { get; }
    }

    /// <summary>A server-streaming method: one request, a stream of responses.</summary>
    public sealed class ServerStreamingMethod<TRequest, TResponse> : GrpcMethod
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        /// <summary>Resolves <paramref name="serviceFullName"/>/<paramref name="methodName"/> in <paramref name="file"/> and checks its types and kind.</summary>
        public ServerStreamingMethod(FileDescriptor file, string serviceFullName, string methodName)
            : base(file, serviceFullName, methodName, MethodKind.ServerStreaming, typeof(TRequest), typeof(TResponse))
        {
            RequestCodec = CodecFor<TRequest>(Descriptor.InputType);
            ResponseCodec = CodecFor<TResponse>(Descriptor.OutputType);
        }

        internal ProtobufCodec<TRequest> RequestCodec { get; }

        internal ProtobufCodec<TResponse> ResponseCodec { get; }
    }

    /// <summary>A client-streaming method: a stream of requests, one response.</summary>
    public sealed class ClientStreamingMethod<TRequest, TResponse> : GrpcMethod
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        /// <summary>Resolves <paramref name="serviceFullName"/>/<paramref name="methodName"/> in <paramref name="file"/> and checks its types and kind.</summary>
        public ClientStreamingMethod(FileDescriptor file, string serviceFullName, string methodName)
            : base(file, serviceFullName, methodName, MethodKind.ClientStreaming, typeof(TRequest), typeof(TResponse))
        {
            RequestCodec = CodecFor<TRequest>(Descriptor.InputType);
            ResponseCodec = CodecFor<TResponse>(Descriptor.OutputType);
        }

        internal ProtobufCodec<TRequest> RequestCodec { get; }

        internal ProtobufCodec<TResponse> ResponseCodec { get; }
    }

    /// <summary>A bidirectional streaming method.</summary>
    public sealed class DuplexMethod<TRequest, TResponse> : GrpcMethod
        where TRequest : class, IMessage<TRequest>
        where TResponse : class, IMessage<TResponse>
    {
        /// <summary>Resolves <paramref name="serviceFullName"/>/<paramref name="methodName"/> in <paramref name="file"/> and checks its types and kind.</summary>
        public DuplexMethod(FileDescriptor file, string serviceFullName, string methodName)
            : base(file, serviceFullName, methodName, MethodKind.DuplexStreaming, typeof(TRequest), typeof(TResponse))
        {
            RequestCodec = CodecFor<TRequest>(Descriptor.InputType);
            ResponseCodec = CodecFor<TResponse>(Descriptor.OutputType);
        }

        internal ProtobufCodec<TRequest> RequestCodec { get; }

        internal ProtobufCodec<TResponse> ResponseCodec { get; }
    }
}
