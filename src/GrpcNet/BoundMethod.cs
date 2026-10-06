using System;

namespace GrpcNet
{
    /// <summary>
    /// A read-only view of one registered method, for the checks a <see cref="GrpcProcessorBuilder"/> runs before it builds a
    /// processor. Never consulted while calls are processed.
    /// </summary>
    public sealed class BoundMethod
    {
        internal BoundMethod(string path, MethodKind kind, Type requestType, Type responseType, object? contract)
        {
            Path = path;
            Kind = kind;
            RequestType = requestType;
            ResponseType = responseType;
            Contract = contract;
        }

        /// <summary>The method's path, <c>/package.Service/Method</c>.</summary>
        public string Path { get; }

        /// <summary>The call kind the method was bound as.</summary>
        public MethodKind Kind { get; }

        /// <summary>The request message type the handler takes.</summary>
        public Type RequestType { get; }

        /// <summary>The response message type the handler returns.</summary>
        public Type ResponseType { get; }

        /// <summary>
        /// What the binding claims to implement, as supplied by the registering adapter (for example a protobuf method
        /// descriptor), or null. The core never reads it.
        /// </summary>
        public object? Contract { get; }

        /// <summary>The service part of <see cref="Path"/>: <c>package.Service</c>.</summary>
        public string ServiceName => Path.Substring(1, Path.IndexOf('/', 1) - 1);

        /// <summary>The method part of <see cref="Path"/>.</summary>
        public string MethodName => Path.Substring(Path.IndexOf('/', 1) + 1);
    }
}
