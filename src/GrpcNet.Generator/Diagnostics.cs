using System.Linq;
using Microsoft.CodeAnalysis;

namespace GrpcNet.Generator
{
    internal static class Diagnostics
    {
        public const string UnreadableDescriptor = "GN0001";
        public const string UnresolvedType = "GN0002";
        public const string Collision = "GN0003";
        public const string NotIntercepted = "GN0004";
        public const string InternalError = "GN0099";

        private const string Category = "GrpcNet";

        private static readonly DiagnosticDescriptor s_unreadable = new DiagnosticDescriptor(
            UnreadableDescriptor,
            "The embedded descriptor cannot be read",
            "The descriptor embedded in '{0}' cannot be read: {1}. No GrpcNet code is generated for this file.",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor s_unresolved = new DiagnosticDescriptor(
            UnresolvedType,
            "A message type cannot be resolved",
            "The message type '{1}' of the method '{0}' cannot be resolved to a C# type: {2}. Generate it with protoc in this project or in a referenced one.",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor s_collision = new DiagnosticDescriptor(
            Collision,
            "The generated service class collides with existing code",
            "The GrpcNet class for the service '{0}' cannot be generated: {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor s_notIntercepted = new DiagnosticDescriptor(
            NotIntercepted,
            "Method-group handlers are not bound as direct calls",
            "{0} handler(s) bound as method groups keep their delegate, because the project does not opt in to GrpcNet's interceptors. Add GrpcNet.Generated to the InterceptorsNamespaces property to bind them as direct calls.",
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor s_internal = new DiagnosticDescriptor(
            InternalError,
            "The generator failed",
            "The GrpcNet generator failed on '{0}': {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static Diagnostic Create(DiagnosticInfo info)
        {
            DiagnosticDescriptor descriptor = info.Id switch
            {
                UnreadableDescriptor => s_unreadable,
                UnresolvedType => s_unresolved,
                Collision => s_collision,
                NotIntercepted => s_notIntercepted,
                _ => s_internal,
            };
            return Diagnostic.Create(descriptor, info.Location?.ToLocation() ?? Location.None, info.Arguments.Cast<object>().ToArray());
        }
    }
}
