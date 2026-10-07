using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace GrpcNet.Protobuf
{
    /// <summary>A generated binder: binds a handler to every method of one service.</summary>
    public interface IServiceBinder
    {
        /// <summary>
        /// Binds every method of the service and requires the service to be complete. Nothing is bound if any handler is
        /// missing or any method is already bound.
        /// </summary>
        void BindTo(GrpcProcessorBuilder builder);
    }

    /// <summary>Helpers called by generated code. Not for direct use.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class GeneratedSupport
    {
        /// <summary>
        /// Runs the static initializer of a generated service class, which creates and checks its method objects. A method object
        /// that does not match its <c>.proto</c> throws its own exception here, not one wrapped in a
        /// <see cref="TypeInitializationException"/>.
        /// </summary>
        public static void EnsureInitialized([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type serviceClass)
        {
            if (serviceClass == null) throw new ArgumentNullException(nameof(serviceClass));
            try
            {
                RuntimeHelpers.RunClassConstructor(serviceClass.TypeHandle);
            }
            catch (TypeInitializationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        /// <summary>Finds <paramref name="serviceFullName"/> in <paramref name="file"/>, or throws if the file has fallen behind.</summary>
        public static ServiceDescriptor FindService(FileDescriptor file, string serviceFullName)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            foreach (ServiceDescriptor service in file.Services)
            {
                if (service.FullName == serviceFullName)
                {
                    return service;
                }
            }

            throw new InvalidOperationException("The service '" + serviceFullName + "' is not declared in '" + file.Name + "'. The generated code is out of date with its .proto.");
        }

        /// <summary>Binds a generated binder's unary member in the form it was made in.</summary>
        public static GrpcProcessorBuilder BindUnary<TRequest, TResponse>(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, UnaryHandler<TRequest, TResponse> handler)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
        {
            if (handler.Origin == null)
            {
                throw new ArgumentException("The handler for '" + method.Path + "' is empty.", nameof(handler));
            }

            if (!ReferenceEquals(handler.Origin, method))
            {
                throw new ArgumentException("The handler for '" + method.Path + "' was made from '" + handler.Origin.Path + "'.", nameof(handler));
            }

            if (handler.Direct != null)
            {
                return handler.Direct.Bind(builder, method);
            }

            if (handler.Sync != null)
            {
                return builder.Bind(method, handler.Sync);
            }

            if (handler.Tokenless != null)
            {
                return builder.BindAsync(method, handler.Tokenless);
            }

            if (handler.WithToken != null)
            {
                return builder.BindAsync(method, handler.WithToken);
            }

            throw new ArgumentException("The handler for '" + method.Path + "' is empty.", nameof(handler));
        }

        // The direct-call bindings. A generated interceptor replaces a Bind, BindAsync, Sync or Async call whose handler is a
        // method group with one of these, passing a struct that calls the method directly. Each does exactly what the call it
        // replaces does, with the struct in place of the delegate.

        /// <summary>Binds a synchronous unary method to a struct invoker. Same checks and registration as <c>Bind</c>.</summary>
        public static GrpcProcessorBuilder BindSyncUnary<TRequest, TResponse, TInvoker>(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IUnaryInvoker<TRequest, TResponse>
        {
            ProtobufBindings.Preflight(builder, method);
            builder.AddUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, TInvoker>(
                method.Path, method.RequestCodec, method.ResponseCodec, invoker, method.Descriptor);
            return ProtobufBindings.AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds an asynchronous unary method to a struct invoker. Same checks and registration as <c>BindAsync</c>.</summary>
        public static GrpcProcessorBuilder BindAsyncUnary<TRequest, TResponse, TInvoker>(GrpcProcessorBuilder builder, UnaryMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IAsyncUnaryInvoker<TRequest, TResponse>
        {
            ProtobufBindings.Preflight(builder, method);
            builder.AddAsyncUnary<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, TInvoker>(
                method.Path, method.RequestCodec, method.ResponseCodec, invoker, method.Descriptor);
            return ProtobufBindings.AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds a server-streaming method to a struct invoker. Same checks and registration as <c>Bind</c>.</summary>
        public static GrpcProcessorBuilder BindServerStreaming<TRequest, TResponse, TInvoker>(GrpcProcessorBuilder builder, ServerStreamingMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IServerStreamingInvoker<TRequest, TResponse>
        {
            ProtobufBindings.Preflight(builder, method);
            builder.AddServerStreaming<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, TInvoker>(
                method.Path, method.RequestCodec, method.ResponseCodec, invoker, method.Descriptor);
            return ProtobufBindings.AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds a client-streaming method to a struct invoker. Same checks and registration as <c>Bind</c>.</summary>
        public static GrpcProcessorBuilder BindClientStreaming<TRequest, TResponse, TInvoker>(GrpcProcessorBuilder builder, ClientStreamingMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IClientStreamingInvoker<TRequest, TResponse>
        {
            ProtobufBindings.Preflight(builder, method);
            builder.AddClientStreaming<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, TInvoker>(
                method.Path, method.RequestCodec, method.ResponseCodec, invoker, method.Descriptor);
            return ProtobufBindings.AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>Binds a bidirectional streaming method to a struct invoker. Same checks and registration as <c>Bind</c>.</summary>
        public static GrpcProcessorBuilder BindDuplex<TRequest, TResponse, TInvoker>(GrpcProcessorBuilder builder, DuplexMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IDuplexInvoker<TRequest, TResponse>
        {
            ProtobufBindings.Preflight(builder, method);
            builder.AddDuplex<TRequest, TResponse, ProtobufCodec<TRequest>, ProtobufCodec<TResponse>, TInvoker>(
                method.Path, method.RequestCodec, method.ResponseCodec, invoker, method.Descriptor);
            return ProtobufBindings.AddService(builder, method.Descriptor.Service, requireComplete: false);
        }

        /// <summary>A binder handler for <paramref name="method"/> that binds the synchronous struct <paramref name="invoker"/>.</summary>
        public static UnaryHandler<TRequest, TResponse> SyncHandler<TRequest, TResponse, TInvoker>(UnaryMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IUnaryInvoker<TRequest, TResponse>
            => new UnaryHandler<TRequest, TResponse>(method ?? throw new ArgumentNullException(nameof(method)), null, null, null, new SyncUnaryBinding<TRequest, TResponse, TInvoker>(invoker));

        /// <summary>A binder handler for <paramref name="method"/> that binds the asynchronous struct <paramref name="invoker"/>.</summary>
        public static UnaryHandler<TRequest, TResponse> AsyncHandler<TRequest, TResponse, TInvoker>(UnaryMethod<TRequest, TResponse> method, TInvoker invoker)
            where TRequest : class, IMessage<TRequest>
            where TResponse : class, IMessage<TResponse>
            where TInvoker : struct, IAsyncUnaryInvoker<TRequest, TResponse>
            => new UnaryHandler<TRequest, TResponse>(method ?? throw new ArgumentNullException(nameof(method)), null, null, null, new AsyncUnaryBinding<TRequest, TResponse, TInvoker>(invoker));

        /// <summary>
        /// Checks everything that could reject a binder before anything is bound: every handler is present, every unary handler
        /// was made from its own method object, no method is already bound, and the service is not registered from another
        /// descriptor.
        /// </summary>
        public static void PreflightService(GrpcProcessorBuilder builder, ServiceDescriptor service, GrpcMethod[] methods, object?[] handlers, string[] names)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (methods == null) throw new ArgumentNullException(nameof(methods));
            if (handlers == null) throw new ArgumentNullException(nameof(handlers));
            if (names == null) throw new ArgumentNullException(nameof(names));
            if (methods.Length != handlers.Length || methods.Length != names.Length)
            {
                throw new ArgumentException("The generated binder passed lists of different lengths.");
            }

            for (int i = 0; i < handlers.Length; i++)
            {
                object? handler = handlers[i];
                if (handler == null || (handler is IUnaryHandler { Origin: null }))
                {
                    throw new ArgumentException("The binder for '" + service.FullName + "' has no handler for '" + names[i] + "'.", nameof(handlers));
                }

                // A unary handler is made from a method object; one made from another method, even one with the same types,
                // would bind the wrong code.
                if (handler is IUnaryHandler { Origin: GrpcMethod origin } && !ReferenceEquals(origin, methods[i]))
                {
                    throw new ArgumentException("The binder for '" + service.FullName + "' has a handler for '" + names[i] + "' made from '" + origin.Path + "'.", nameof(handlers));
                }
            }

            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (BoundMethod binding in builder.Bindings)
            {
                bound.Add(binding.Path);
            }

            var already = new List<string>();
            foreach (GrpcMethod method in methods)
            {
                if (bound.Contains(method.Path))
                {
                    already.Add(method.Path);
                }
            }

            if (already.Count > 0)
            {
                throw new InvalidOperationException("The service '" + service.FullName + "' cannot be bound by its binder: " + string.Join(", ", already) + " is already bound.");
            }

            ProtobufBindings.ThrowIfConflicting(builder, service);
        }
    }
}
