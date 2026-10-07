using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace GrpcNet.Generator
{
    /// <summary>The call a site makes, which fixes the interceptor's signature and the invoker it builds.</summary>
    internal enum SiteKind
    {
        BindUnary,
        BindAsyncTokenless,
        BindAsyncWithToken,
        BindServerStreaming,
        BindClientStreaming,
        BindDuplex,
        Sync,
        AsyncTokenless,
        AsyncWithToken,
    }

    /// <summary>
    /// One call whose handler is a method group the generator can call directly. Types are fully qualified C# names; the
    /// location is Roslyn's opaque interceptable-location encoding.
    /// </summary>
    internal sealed record InterceptSite(
        SiteKind Kind,
        string Request,
        string Response,
        bool IsStatic,
        string TargetType,
        string TargetMethod,
        int Version,
        string Data,
        string Display,
        LocationInfo? Location);

    /// <summary>
    /// A call on a method object this generator emits. A generator's input does not contain its own output, so the call does
    /// not bind there; it is matched against the generated services instead, and kept only if the handler's signature is
    /// exactly the one the bound call will need.
    /// </summary>
    internal sealed record PendingSite(
        string Call,
        string Qualifier,
        bool QualifierIsExact,
        string MethodName,
        EquatableArray<string> HandlerParameters,
        string HandlerReturn,
        bool IsStatic,
        string TargetType,
        string TargetMethod,
        int Version,
        string Data,
        string Display,
        LocationInfo? Location);

    /// <summary>What the syntax pass found at one call: a site ready to emit, or one to match against the generated services.</summary>
    internal sealed record SiteCandidate(InterceptSite? Resolved, PendingSite? Pending);

    /// <summary>A method this generator emits a method object for, with its class's full name.</summary>
    internal sealed record GeneratedMethod(string ClassName, string Name, CallKind Kind, string Request, string Response);

    /// <summary>
    /// Finds Bind, BindAsync, Sync and Async calls whose handler is a method group, and emits an interceptor per call that binds
    /// a struct invoker calling the method directly. The filter is conservative: anything the generated file-local code could
    /// not name, reach or call with exactly the semantics of the delegate keeps the delegate.
    /// </summary>
    internal static class Interceptors
    {
        public const string Namespace = "GrpcNet.Generated";
        public const string HintName = "GrpcNet.Interceptors.g.cs";

        private const string Core = "global::GrpcNet.";
        private const string Protobuf = "global::GrpcNet.Protobuf.";
        private const string Token = "global::System.Threading.CancellationToken";
        private const string ValueTask = "global::System.Threading.Tasks.ValueTask";

        private static readonly SymbolDisplayFormat s_typeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        /// <summary>Whether the project lists <see cref="Namespace"/> in InterceptorsNamespaces, which the compiler requires.</summary>
        public static bool OptedIn(ParseOptions options)
        {
            foreach (string feature in new[] { "InterceptorsNamespaces", "InterceptorsPreviewNamespaces" })
            {
                if (options.Features.TryGetValue(feature, out string? value)
                    && value.Split(';').Any(n => n.Trim() == Namespace))
                {
                    return true;
                }
            }

            return false;
        }

        // Syntax only: a call named like one of the binding methods with an argument that could be a method group.
        public static bool IsCandidate(SyntaxNode node)
        {
            if (node is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            string? name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name is IdentifierNameSyntax id ? id.Identifier.ValueText : null,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null,
            };
            if (name is not ("Bind" or "BindAsync" or "Sync" or "Async"))
            {
                return false;
            }

            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (argument.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
                {
                    return true;
                }
            }

            return false;
        }

        public static SiteCandidate? Extract(GeneratorSyntaxContext context, CancellationToken cancellationToken)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            SemanticModel model = context.SemanticModel;
            if (model.GetOperation(invocation, cancellationToken) is IInvocationOperation call
                && Classify(call.TargetMethod) is SiteKind kind
                && !MessageTypes(call, kind).Any(t => t is IErrorTypeSymbol))
            {
                InterceptSite? site = FromBoundCall(model, invocation, call, kind, cancellationToken);
                return site == null ? null : new SiteCandidate(site, null);
            }

            PendingSite? pending = FromUnboundCall(model, invocation, cancellationToken);
            return pending == null ? null : new SiteCandidate(null, pending);
        }

        // Bind and BindAsync carry the message types as method type arguments; Sync and Async, on their method object.
        private static ImmutableArray<ITypeSymbol> MessageTypes(IInvocationOperation call, SiteKind kind)
            => kind is SiteKind.Sync or SiteKind.AsyncTokenless or SiteKind.AsyncWithToken
                ? call.TargetMethod.ContainingType.TypeArguments
                : call.TargetMethod.TypeArguments;

        // A call that binds in the input: a method object written by hand or generated in a referenced assembly.
        private static InterceptSite? FromBoundCall(SemanticModel model, InvocationExpressionSyntax invocation, IInvocationOperation call, SiteKind kind, CancellationToken cancellationToken)
        {
            IArgumentOperation? handler = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "handler");
            if (handler?.Value is not IDelegateCreationOperation { Target: IMethodReferenceOperation reference })
            {
                return null;
            }

            ImmutableArray<ITypeSymbol> messages = MessageTypes(call, kind);
            Compilation compilation = model.Compilation;
            IMethodSymbol method = reference.Method;
            if (messages.Length != 2
                || !CanCallDirectly(compilation, method, reference.Instance?.Syntax is BaseExpressionSyntax, reference.Instance?.Type)
                || !Nameable(compilation, messages[0]) || !Nameable(compilation, messages[1]))
            {
                return null;
            }

            InterceptableLocation? location = Locate(model, invocation, cancellationToken);
            if (location == null)
            {
                return null;
            }

            return new InterceptSite(
                kind,
                messages[0].ToDisplayString(s_typeFormat),
                messages[1].ToDisplayString(s_typeFormat),
                method.IsStatic,
                method.ContainingType.ToDisplayString(s_typeFormat),
                Emitter.Identifier(method.Name),
                location.Version,
                location.Data,
                location.GetDisplayLocation(),
                LocationInfo.From(invocation.GetLocation()));
        }

        // A call on a method object generated in this compilation, which does not bind in the input. Only the plain shapes are
        // taken: builder.Bind(Class.Method, handler), ProtobufBindings.Bind(builder, Class.Method, handler) and
        // Class.Method.Sync(handler), with positional arguments.
        private static PendingSite? FromUnboundCall(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } access)
            {
                return null;
            }

            SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
            if (arguments.Any(a => a.NameColon != null || !a.RefKindKeyword.IsKind(SyntaxKind.None)))
            {
                return null;
            }

            string call = name.Identifier.ValueText;
            ExpressionSyntax methodObject;
            ExpressionSyntax handler;
            if (call is "Sync" or "Async")
            {
                if (arguments.Count != 1)
                {
                    return null;
                }

                methodObject = access.Expression;
                handler = arguments[0].Expression;
            }
            else if (arguments.Count == 2 && IsType(model.GetTypeInfo(access.Expression, cancellationToken).Type, "GrpcNet.GrpcProcessorBuilder"))
            {
                // Every method of this name the call could bind to must be ProtobufBindings', so the intercepted target is certain.
                ITypeSymbol builder = model.GetTypeInfo(access.Expression, cancellationToken).Type!;
                ImmutableArray<ISymbol> visible = model.LookupSymbols(invocation.SpanStart, builder, call, includeReducedExtensionMethods: true);
                if (visible.IsEmpty || visible.Any(v => v is not IMethodSymbol m || !IsType((m.ReducedFrom ?? m).ContainingType, "GrpcNet.Protobuf.ProtobufBindings")))
                {
                    return null;
                }

                methodObject = arguments[0].Expression;
                handler = arguments[1].Expression;
            }
            else if (arguments.Count == 3
                && model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is INamedTypeSymbol owner && IsType(owner, "GrpcNet.Protobuf.ProtobufBindings")
                && IsType(model.GetTypeInfo(arguments[0].Expression, cancellationToken).Type, "GrpcNet.GrpcProcessorBuilder"))
            {
                methodObject = arguments[1].Expression;
                handler = arguments[2].Expression;
            }
            else
            {
                return null;
            }

            // The method object is written Class.Method and names nothing in the input, so it can only be generated code.
            if (methodObject is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax methodName } objectAccess
                || DottedName(objectAccess.Expression) is not string qualifier
                || model.GetSymbolInfo(methodObject, cancellationToken).Symbol != null)
            {
                return null;
            }

            bool exact = false;
            if (model.GetSymbolInfo(objectAccess.Expression, cancellationToken).Symbol is INamedTypeSymbol existing)
            {
                // The class exists (a partial written by the user, or Grpc's own service class) but lacks the member.
                if (!existing.GetMembers(methodName.Identifier.ValueText).IsEmpty)
                {
                    return null;
                }

                qualifier = existing.ToDisplayString();
                exact = true;
            }

            // The handler: a method group naming exactly one method.
            if (handler is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                return null;
            }

            SymbolInfo info = model.GetSymbolInfo(handler, cancellationToken);
            IMethodSymbol? method = info.Symbol as IMethodSymbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] as IMethodSymbol : null);
            if (method == null)
            {
                return null;
            }

            ITypeSymbol? receiver = method.IsStatic
                ? null
                : handler is MemberAccessExpressionSyntax member
                    ? model.GetTypeInfo(member.Expression, cancellationToken).Type
                    : model.GetEnclosingSymbol(invocation.SpanStart, cancellationToken)?.ContainingType;
            bool viaBase = handler is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax };
            if (!CanCallDirectly(model.Compilation, method, viaBase, receiver))
            {
                return null;
            }

            InterceptableLocation? location = Locate(model, invocation, cancellationToken);
            if (location == null)
            {
                return null;
            }

            return new PendingSite(
                call,
                qualifier,
                exact,
                methodName.Identifier.ValueText,
                new EquatableArray<string>(method.Parameters.Select(p => p.Type.ToDisplayString(s_typeFormat)).ToArray()),
                method.ReturnType.ToDisplayString(s_typeFormat),
                method.IsStatic,
                method.ContainingType.ToDisplayString(s_typeFormat),
                Emitter.Identifier(method.Name),
                location.Version,
                location.Data,
                location.GetDisplayLocation(),
                LocationInfo.From(invocation.GetLocation()));
        }

        /// <summary>
        /// Matches a pending site to the one generated method its Class.Method names, and keeps it only when the handler's
        /// parameter and return types are exactly the ones the bound call will convert it to.
        /// </summary>
        public static InterceptSite? Resolve(PendingSite site, EquatableArray<GeneratedMethod> generated)
        {
            GeneratedMethod[] matches = generated
                .Where(g => g.Name == site.MethodName
                    && (g.ClassName == site.Qualifier || (!site.QualifierIsExact && g.ClassName.EndsWith("." + site.Qualifier, StringComparison.Ordinal))))
                .ToArray();
            if (matches.Length != 1)
            {
                return null;
            }

            GeneratedMethod target = matches[0];
            SiteKind? kind = (site.Call, target.Kind, site.HandlerParameters.Count) switch
            {
                ("Bind", CallKind.Unary, _) => SiteKind.BindUnary,
                ("BindAsync", CallKind.Unary, 1) => SiteKind.BindAsyncTokenless,
                ("BindAsync", CallKind.Unary, 2) => SiteKind.BindAsyncWithToken,
                ("Bind", CallKind.ServerStreaming, _) => SiteKind.BindServerStreaming,
                ("Bind", CallKind.ClientStreaming, _) => SiteKind.BindClientStreaming,
                ("Bind", CallKind.Duplex, _) => SiteKind.BindDuplex,
                ("Sync", CallKind.Unary, _) => SiteKind.Sync,
                ("Async", CallKind.Unary, 1) => SiteKind.AsyncTokenless,
                ("Async", CallKind.Unary, 2) => SiteKind.AsyncWithToken,
                _ => null,
            };
            if (kind == null)
            {
                return null;
            }

            (string[] parameters, string returns) = HandlerSignature(kind.Value, target.Request, target.Response);
            if (!site.HandlerParameters.SequenceEqual(parameters) || site.HandlerReturn != returns)
            {
                return null;
            }

            return new InterceptSite(kind.Value, target.Request, target.Response, site.IsStatic, site.TargetType, site.TargetMethod, site.Version, site.Data, site.Display, site.Location);
        }

        /// <summary>Every method object the generator emits: from files without errors, in classes only one file generates.</summary>
        public static EquatableArray<GeneratedMethod> Generated(ImmutableArray<FileModel> files)
        {
            var classes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (FileModel file in files)
            {
                foreach (ServiceModel service in file.Services)
                {
                    string name = Emitter.ClassName(file, service);
                    classes[name] = classes.TryGetValue(name, out int count) ? count + 1 : 1;
                }
            }

            var methods = new List<GeneratedMethod>();
            foreach (FileModel file in files)
            {
                if (file.Diagnostics.Count > 0)
                {
                    continue;
                }

                foreach (ServiceModel service in file.Services)
                {
                    string name = Emitter.ClassName(file, service);
                    if (classes[name] == 1)
                    {
                        methods.AddRange(service.Methods.Select(m => new GeneratedMethod(name, m.Name, m.Kind, m.RequestType, m.ResponseType)));
                    }
                }
            }

            return new EquatableArray<GeneratedMethod>(methods.ToArray());
        }

        private static (string[] Parameters, string Returns) HandlerSignature(SiteKind kind, string q, string r)
        {
            string writer = Core + "MessageWriter<" + r + ">";
            string reader = Core + "MessageReader<" + q + ">";
            return kind switch
            {
                SiteKind.BindUnary or SiteKind.Sync => (new[] { q }, r),
                SiteKind.BindAsyncTokenless or SiteKind.AsyncTokenless => (new[] { q }, ValueTask + "<" + r + ">"),
                SiteKind.BindAsyncWithToken or SiteKind.AsyncWithToken => (new[] { q, Token }, ValueTask + "<" + r + ">"),
                SiteKind.BindServerStreaming => (new[] { q, writer, Token }, ValueTask),
                SiteKind.BindClientStreaming => (new[] { reader, Token }, ValueTask + "<" + r + ">"),
                _ => (new[] { reader, writer, Token }, ValueTask),
            };
        }

        private static InterceptableLocation? Locate(SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
        {
#pragma warning disable RSEXPERIMENTAL002 // The interceptable-location API is stable in the compiler that ships it.
            return model.GetInterceptableLocation(invocation, cancellationToken);
#pragma warning restore RSEXPERIMENTAL002
        }

        private static bool IsType(ITypeSymbol? type, string fullName)
            => type is INamedTypeSymbol named && named.ToDisplayString() == fullName
                && named.ContainingAssembly?.Name == fullName.Substring(0, fullName.LastIndexOf('.'));

        // A.B.C or global::A.B.C, as written; null for anything else.
        private static string? DottedName(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name: IdentifierNameSyntax id } => id.Identifier.ValueText,
            MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Name: IdentifierNameSyntax id } member
                => DottedName(member.Expression) is string left ? left + "." + id.Identifier.ValueText : null,
            _ => null,
        };

        private static SiteKind? Classify(IMethodSymbol target)
        {
            INamedTypeSymbol type = target.ContainingType;
            if (type.ContainingNamespace?.ToDisplayString() != "GrpcNet.Protobuf" || type.ContainingAssembly?.Name != "GrpcNet.Protobuf")
            {
                return null;
            }

            IMethodSymbol definition = target.OriginalDefinition;
            if (type.Name == "ProtobufBindings" && definition.Parameters.Length == 3)
            {
                string methodType = definition.Parameters[1].Type.Name;
                int handlerArity = (definition.Parameters[2].Type as INamedTypeSymbol)?.Arity ?? 0;
                return (definition.Name, methodType, handlerArity) switch
                {
                    ("Bind", "UnaryMethod", 2) => SiteKind.BindUnary,
                    ("BindAsync", "UnaryMethod", 2) => SiteKind.BindAsyncTokenless,
                    ("BindAsync", "UnaryMethod", 3) => SiteKind.BindAsyncWithToken,
                    ("Bind", "ServerStreamingMethod", _) => SiteKind.BindServerStreaming,
                    ("Bind", "ClientStreamingMethod", _) => SiteKind.BindClientStreaming,
                    ("Bind", "DuplexMethod", _) => SiteKind.BindDuplex,
                    _ => null,
                };
            }

            if (type.Name == "UnaryMethod" && type.Arity == 2 && definition.Parameters.Length == 1)
            {
                int handlerArity = (definition.Parameters[0].Type as INamedTypeSymbol)?.Arity ?? 0;
                return (definition.Name, handlerArity) switch
                {
                    ("Sync", 2) => SiteKind.Sync,
                    ("Async", 2) => SiteKind.AsyncTokenless,
                    ("Async", 3) => SiteKind.AsyncWithToken,
                    _ => null,
                };
            }

            return null;
        }

        // Whether a call written in a file-local type in this assembly, as `Type.Method(args)` or `((Type)target).Method(args)`,
        // reaches exactly the method the delegate would.
        private static bool CanCallDirectly(Compilation compilation, IMethodSymbol method, bool viaBase, ITypeSymbol? receiver)
        {
            if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod || method.IsExtensionMethod || method.ReducedFrom != null
                || method.ReturnsByRef || method.ReturnsByRefReadonly || method.Parameters.Any(p => p.RefKind != RefKind.None || p.IsParams))
            {
                return false;
            }

            if (!method.IsStatic)
            {
                // base.M binds without virtual dispatch, which a call through the receiver would not reproduce; a struct receiver
                // is boxed into the delegate.
                if (viaBase || !method.ContainingType.IsReferenceType || receiver is not { IsReferenceType: true })
                {
                    return false;
                }
            }

            if (!compilation.IsSymbolAccessibleWithin(method, compilation.Assembly)
                || !Nameable(compilation, method.ContainingType)
                || !Nameable(compilation, method.ReturnType)
                || method.Parameters.Any(p => !Nameable(compilation, p.Type)))
            {
                return false;
            }

            // A name with one method behind it: the direct call cannot pick another overload or a hiding member.
            return CountMethods(method.ContainingType, method.Name) == 1;
        }

        private static int CountMethods(INamedTypeSymbol type, string name)
        {
            var seen = new List<IMethodSymbol>();
            IEnumerable<INamedTypeSymbol> types = type.TypeKind == TypeKind.Interface
                ? new[] { type }.Concat(type.AllInterfaces)
                : Ancestors(type);
            foreach (INamedTypeSymbol t in types)
            {
                foreach (ISymbol member in t.GetMembers(name))
                {
                    if (member is not IMethodSymbol candidate)
                    {
                        return 0;
                    }

                    if (!seen.Any(s => Overrides(s, candidate)))
                    {
                        seen.Add(candidate);
                    }
                }
            }

            return seen.Count;
        }

        private static IEnumerable<INamedTypeSymbol> Ancestors(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
            {
                yield return t;
            }
        }

        private static bool Overrides(IMethodSymbol method, IMethodSymbol candidate)
        {
            for (IMethodSymbol? o = method.OverriddenMethod; o != null; o = o.OverriddenMethod)
            {
                if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, candidate.OriginalDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        // A type the generated file can write out and use: no type parameters, nothing anonymous, file-local, pointer-like or
        // ref-like, and accessible from anywhere in this assembly.
        private static bool Nameable(Compilation compilation, ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol:
                case IErrorTypeSymbol:
                case IPointerTypeSymbol:
                case IFunctionPointerTypeSymbol:
                case IDynamicTypeSymbol:
                    return false;
                case IArrayTypeSymbol array:
                    return Nameable(compilation, array.ElementType);
                case INamedTypeSymbol named:
                    if (named.IsAnonymousType || named.IsRefLikeType || named.IsFileLocal
                        || !compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
                    {
                        return false;
                    }

                    if (named.ContainingType != null && !Nameable(compilation, named.ContainingType))
                    {
                        return false;
                    }

                    return named.TypeArguments.All(t => Nameable(compilation, t));
                default:
                    return false;
            }
        }

        public static string Emit(EquatableArray<InterceptSite> sites)
        {
            var w = new Emitter.Writer();
            w.Line("// <auto-generated/>");
            w.Line("// Generated by GrpcNet.Generator: handlers bound as method groups, called directly. Do not edit.");
            w.Line("#nullable disable");
            w.Line();
            w.Line("namespace System.Runtime.CompilerServices");
            w.Open();
            w.Line("[global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
            w.Line("file sealed class InterceptsLocationAttribute : global::System.Attribute");
            w.Open();
            w.Line("public InterceptsLocationAttribute(int version, string data)");
            w.Open();
            w.Close();
            w.Close();
            w.Close();
            w.Line();
            w.Line("namespace " + Namespace);
            w.Open();
            w.Line("[global::System.CodeDom.Compiler.GeneratedCode(\"GrpcNet.Generator\", \"1.0.0\")]");
            w.Line("file static class GrpcNetInterceptors");
            w.Open();
            for (int i = 0; i < sites.Count; i++)
            {
                if (i > 0)
                {
                    w.Line();
                }

                EmitSite(w, sites[i], i);
            }

            w.Close();
            w.Close();
            return w.ToString();
        }

        private static void EmitSite(Emitter.Writer w, InterceptSite site, int index)
        {
            string q = site.Request;
            string r = site.Response;
            string builder = Core + "GrpcProcessorBuilder";
            string invoker = "Invoker" + index;

            // (the interceptor's leading parameters, the handler type, the return type, the helper and its leading arguments,
            // the invoker interface, the invoker method's signature, and the arguments the target is called with)
            (string head, string handlerType, string returns, string helper, string iface, string signature, string args) = site.Kind switch
            {
                SiteKind.BindUnary => (
                    "this " + builder + " builder, " + Protobuf + "UnaryMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + r + ">", builder, "BindSyncUnary(builder, method, ",
                    Core + "IUnaryInvoker<" + q + ", " + r + ">", r + " Invoke(" + q + " request)", "request"),
                SiteKind.BindAsyncTokenless => (
                    "this " + builder + " builder, " + Protobuf + "UnaryMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + ValueTask + "<" + r + ">>", builder, "BindAsyncUnary(builder, method, ",
                    Core + "IAsyncUnaryInvoker<" + q + ", " + r + ">", ValueTask + "<" + r + "> InvokeAsync(" + q + " request, " + Token + " cancellation)", "request"),
                SiteKind.BindAsyncWithToken => (
                    "this " + builder + " builder, " + Protobuf + "UnaryMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + Token + ", " + ValueTask + "<" + r + ">>", builder, "BindAsyncUnary(builder, method, ",
                    Core + "IAsyncUnaryInvoker<" + q + ", " + r + ">", ValueTask + "<" + r + "> InvokeAsync(" + q + " request, " + Token + " cancellation)", "request, cancellation"),
                SiteKind.BindServerStreaming => (
                    "this " + builder + " builder, " + Protobuf + "ServerStreamingMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + Core + "MessageWriter<" + r + ">, " + Token + ", " + ValueTask + ">", builder, "BindServerStreaming(builder, method, ",
                    Core + "IServerStreamingInvoker<" + q + ", " + r + ">",
                    ValueTask + " InvokeAsync(" + q + " request, " + Core + "MessageWriter<" + r + "> responses, " + Token + " cancellation)", "request, responses, cancellation"),
                SiteKind.BindClientStreaming => (
                    "this " + builder + " builder, " + Protobuf + "ClientStreamingMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + Core + "MessageReader<" + q + ">, " + Token + ", " + ValueTask + "<" + r + ">>", builder, "BindClientStreaming(builder, method, ",
                    Core + "IClientStreamingInvoker<" + q + ", " + r + ">",
                    ValueTask + "<" + r + "> InvokeAsync(" + Core + "MessageReader<" + q + "> requests, " + Token + " cancellation)", "requests, cancellation"),
                SiteKind.BindDuplex => (
                    "this " + builder + " builder, " + Protobuf + "DuplexMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + Core + "MessageReader<" + q + ">, " + Core + "MessageWriter<" + r + ">, " + Token + ", " + ValueTask + ">", builder, "BindDuplex(builder, method, ",
                    Core + "IDuplexInvoker<" + q + ", " + r + ">",
                    ValueTask + " InvokeAsync(" + Core + "MessageReader<" + q + "> requests, " + Core + "MessageWriter<" + r + "> responses, " + Token + " cancellation)", "requests, responses, cancellation"),
                SiteKind.Sync => (
                    "this " + Protobuf + "UnaryMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + r + ">", Protobuf + "UnaryHandler<" + q + ", " + r + ">", "SyncHandler(method, ",
                    Core + "IUnaryInvoker<" + q + ", " + r + ">", r + " Invoke(" + q + " request)", "request"),
                SiteKind.AsyncTokenless => (
                    "this " + Protobuf + "UnaryMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + ValueTask + "<" + r + ">>", Protobuf + "UnaryHandler<" + q + ", " + r + ">", "AsyncHandler(method, ",
                    Core + "IAsyncUnaryInvoker<" + q + ", " + r + ">", ValueTask + "<" + r + "> InvokeAsync(" + q + " request, " + Token + " cancellation)", "request"),
                _ => (
                    "this " + Protobuf + "UnaryMethod<" + q + ", " + r + "> method",
                    "global::System.Func<" + q + ", " + Token + ", " + ValueTask + "<" + r + ">>", Protobuf + "UnaryHandler<" + q + ", " + r + ">", "AsyncHandler(method, ",
                    Core + "IAsyncUnaryInvoker<" + q + ", " + r + ">", ValueTask + "<" + r + "> InvokeAsync(" + q + " request, " + Token + " cancellation)", "request, cancellation"),
            };

            string create = site.IsStatic ? "new " + invoker + "()" : "new " + invoker + "((" + site.TargetType + ")handler.Target)";
            w.Line("// " + site.Display);
            w.Line("[global::System.Runtime.CompilerServices.InterceptsLocation(" + site.Version + ", \"" + site.Data + "\")]");
            w.Line("public static " + returns + " Intercept" + index + "(" + head + ", " + handlerType + " handler)");
            w.Line("    => " + Protobuf + "GeneratedSupport." + helper + create + ");");
            w.Line();
            w.Line("private readonly struct " + invoker + " : " + iface);
            w.Open();
            if (site.IsStatic)
            {
                w.Line("public " + signature + " => " + site.TargetType + "." + site.TargetMethod + "(" + args + ");");
            }
            else
            {
                w.Line("private readonly " + site.TargetType + " _target;");
                w.Line();
                w.Line("public " + invoker + "(" + site.TargetType + " target) => _target = target;");
                w.Line();
                w.Line("public " + signature + " => _target." + site.TargetMethod + "(" + args + ");");
            }

            w.Close();
        }
    }
}
