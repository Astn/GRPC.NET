using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GrpcNet.Protobuf
{
    /// <summary>Binds handlers to typed method objects and checks every binding against its <c>.proto</c> service.</summary>
    public static class ProtobufBindings
    {
        private static readonly ConditionalWeakTable<GrpcProcessorBuilder, ServiceRegistry> s_registries = new ConditionalWeakTable<GrpcProcessorBuilder, ServiceRegistry>();

        /// <summary>
        /// Binds <paramref name="handler"/> to the unary <paramref name="method"/>. The handler's request and response types are
        /// inferred from the method object.
        /// </summary>
        public static GrpcProcessorBuilder Bind<TRequest, TResponse>(this GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, TResponse> handler)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            Preflight(builder, method);
            builder.AddUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>>(
                method.Path, method.RequestCodec, method.ResponseCodec, handler, method.Descriptor);
            return AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds an asynchronous <paramref name="handler"/> to the unary <paramref name="method"/>.</summary>
        public static GrpcProcessorBuilder Bind<TRequest, TResponse>(this GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            Preflight(builder, method);
            builder.AddAsyncUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>>(
                method.Path, method.RequestCodec, method.ResponseCodec, handler, method.Descriptor);
            return AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds <paramref name="handler"/> to the server-streaming <paramref name="method"/>.</summary>
        public static GrpcProcessorBuilder Bind<TRequest, TResponse>(this GrpcProcessorBuilder builder, ServerStreamingMethod<TRequest, TResponse> method, Func<TRequest, MessageWriter<TResponse>, CancellationToken, ValueTask> handler)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            Preflight(builder, method);
            builder.AddServerStreaming<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>>(
                method.Path, method.RequestCodec, method.ResponseCodec, handler, method.Descriptor);
            return AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds <paramref name="handler"/> to the client-streaming <paramref name="method"/>.</summary>
        public static GrpcProcessorBuilder Bind<TRequest, TResponse>(this GrpcProcessorBuilder builder, ClientStreamingMethod<TRequest, TResponse> method, Func<MessageReader<TRequest>, CancellationToken, ValueTask<TResponse>> handler)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            Preflight(builder, method);
            builder.AddClientStreaming<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>>(
                method.Path, method.RequestCodec, method.ResponseCodec, handler, method.Descriptor);
            return AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds <paramref name="handler"/> to the bidirectional streaming <paramref name="method"/>.</summary>
        public static GrpcProcessorBuilder Bind<TRequest, TResponse>(this GrpcProcessorBuilder builder, DuplexMethod<TRequest, TResponse> method, Func<MessageReader<TRequest>, MessageWriter<TResponse>, CancellationToken, ValueTask> handler)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            Preflight(builder, method);
            builder.AddDuplex<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>>(
                method.Path, method.RequestCodec, method.ResponseCodec, handler, method.Descriptor);
            return AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>
        /// Binds every method of a service through its generated <paramref name="binder"/>, and requires the service to be
        /// complete when the processor is built.
        /// </summary>
        public static GrpcProcessorBuilder Bind(this GrpcProcessorBuilder builder, IServiceBinder binder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (binder == null) throw new ArgumentNullException(nameof(binder));
            binder.BindTo(builder);
            return builder;
        }

        // Everything that can reject a Bind is checked before the builder changes, so a rejected Bind leaves nothing behind.
        private static void Preflight(GrpcProcessorBuilder builder, GrpcMethod method)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (method == null) throw new ArgumentNullException(nameof(method));
            ThrowIfConflicting(builder, method.Descriptor.Service);
        }

        internal static void ThrowIfConflicting(GrpcProcessorBuilder builder, ServiceDescriptor service)
        {
            if (s_registries.TryGetValue(builder, out ServiceRegistry? registry))
            {
                registry.ThrowIfConflicting(service);
            }
        }

        /// <summary>
        /// Registers <paramref name="service"/> so that every method bound under its name, typed or raw, is checked against it
        /// when the processor is built: the method exists, its request and response types and call kind match the descriptor,
        /// and a typed binding's descriptor is the one its path names. With <paramref name="requireComplete"/>, every method the
        /// service declares must be bound. Registering a service again can only add the completeness requirement.
        /// </summary>
        public static GrpcProcessorBuilder AddService(this GrpcProcessorBuilder builder, ServiceDescriptor service, bool requireComplete)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (service == null) throw new ArgumentNullException(nameof(service));

            ServiceRegistry registry = s_registries.GetValue(builder, static b =>
            {
                var created = new ServiceRegistry();
                b.AddCheck(created.Check);
                return created;
            });
            registry.Add(service, requireComplete);
            return builder;
        }

        private sealed class ServiceRegistry
        {
            private readonly List<Registration> _services = new List<Registration>();

            internal void Add(ServiceDescriptor service, bool requireComplete)
            {
                ThrowIfConflicting(service);
                Registration? existing = Find(service.FullName);
                if (existing != null)
                {
                    existing.RequireComplete |= requireComplete;
                    return;
                }

                _services.Add(new Registration(service, requireComplete));
            }

            internal void ThrowIfConflicting(ServiceDescriptor service)
            {
                Registration? existing = Find(service.FullName);
                if (existing != null && !ReferenceEquals(existing.Service, service))
                {
                    throw new InvalidOperationException("The service '" + service.FullName + "' is registered from two different descriptors.");
                }
            }

            internal void Check(IReadOnlyList<BoundMethod> bindings, ICollection<string> problems)
            {
                var bound = new HashSet<string>(StringComparer.Ordinal);
                foreach (BoundMethod binding in bindings)
                {
                    Registration? registration = Find(binding.ServiceName);
                    if (registration == null)
                    {
                        continue;
                    }

                    bound.Add(binding.Path);
                    MethodDescriptor? method = registration.Service.FindMethodByName(binding.MethodName);
                    if (method == null)
                    {
                        problems.Add("'" + binding.Path + "' is bound, but the service '" + binding.ServiceName + "' declares no method '" + binding.MethodName + "'.");
                        continue;
                    }

                    string? mismatch = ProtobufContract.Mismatch(method, binding.Kind, binding.RequestType, binding.ResponseType);
                    if (mismatch != null)
                    {
                        problems.Add("'" + binding.Path + "' " + mismatch + ".");
                    }

                    if (binding.Contract is MethodDescriptor claimed && !ReferenceEquals(claimed, method))
                    {
                        problems.Add("'" + binding.Path + "' is bound with the method object for '" + claimed.FullName + "'.");
                    }
                }

                foreach (Registration registration in _services)
                {
                    if (!registration.RequireComplete)
                    {
                        continue;
                    }

                    var missing = new List<string>();
                    foreach (MethodDescriptor method in registration.Service.Methods)
                    {
                        if (!bound.Contains("/" + registration.Service.FullName + "/" + method.Name))
                        {
                            missing.Add(method.Name);
                        }
                    }

                    if (missing.Count > 0)
                    {
                        problems.Add("The service '" + registration.Service.FullName + "' must have every method bound; unbound: " + string.Join(", ", missing) + ".");
                    }
                }
            }

            private Registration? Find(string serviceFullName)
            {
                foreach (Registration registration in _services)
                {
                    if (registration.Service.FullName == serviceFullName)
                    {
                        return registration;
                    }
                }

                return null;
            }
        }

        private sealed class Registration
        {
            internal Registration(ServiceDescriptor service, bool requireComplete)
            {
                Service = service;
                RequireComplete = requireComplete;
            }

            internal ServiceDescriptor Service { get; }

            internal bool RequireComplete { get; set; }
        }
    }

    internal static class ProtobufContract
    {
        internal static MethodKind KindOf(MethodDescriptor method)
        {
            return method.IsClientStreaming
                ? (method.IsServerStreaming ? MethodKind.DuplexStreaming : MethodKind.ClientStreaming)
                : (method.IsServerStreaming ? MethodKind.ServerStreaming : MethodKind.Unary);
        }

        /// <summary>Describes how a binding differs from <paramref name="method"/>, or returns null when it matches.</summary>
        internal static string? Mismatch(MethodDescriptor method, MethodKind kind, Type requestType, Type responseType)
        {
            var differences = new List<string>();
            MethodKind declared = KindOf(method);
            if (kind != declared)
            {
                differences.Add("is bound as " + kind + ", but the .proto declares " + declared);
            }

            if (requestType != method.InputType.ClrType)
            {
                differences.Add("takes " + requestType.FullName + ", but the .proto declares " + method.InputType.FullName + " (" + method.InputType.ClrType?.FullName + ")");
            }

            if (responseType != method.OutputType.ClrType)
            {
                differences.Add("returns " + responseType.FullName + ", but the .proto declares " + method.OutputType.FullName + " (" + method.OutputType.ClrType?.FullName + ")");
            }

            return differences.Count == 0 ? null : string.Join("; ", differences);
        }
    }
}
