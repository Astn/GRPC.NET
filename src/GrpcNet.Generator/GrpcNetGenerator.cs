using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GrpcNet.Generator
{
    /// <summary>
    /// Generates GrpcNet method objects and service binders for every service declared in a <c>.proto</c> file whose C# protoc
    /// generated in this compilation. The descriptor is read from the serialized copy protoc embeds in the file's reflection
    /// class, and message types are resolved through the compilation, so the generator repeats none of protoc's naming rules for
    /// the messages it can see.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class GrpcNetGenerator : IIncrementalGenerator
    {
        internal const string FileModelsStep = "FileModels";
        internal const string EnabledStep = "Enabled";
        internal const string DuplicatesStep = "Duplicates";

        private const string FileDescriptorType = "Google.Protobuf.Reflection.FileDescriptor";
        private const string GrpcMethodType = "GrpcNet.Protobuf.GrpcMethod";

        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<FileModel> files = context.SyntaxProvider
                .CreateSyntaxProvider(static (node, _) => IsCandidate(node), static (ctx, ct) => Extract(ctx, ct))
                .Where(static model => model != null)
                .Select(static (model, _) => model!)
                .WithTrackingName(FileModelsStep);

            // Nothing is generated unless the compilation can use the output.
            IncrementalValueProvider<bool> enabled = context.CompilationProvider
                .Select(static (compilation, _) => compilation.GetTypeByMetadataName(GrpcMethodType) != null)
                .WithTrackingName(EnabledStep);

            // Two files can map services to the same C# class: different packages, one csharp_namespace. Every class this
            // generator would emit more than once is found across all files before any file is emitted.
            IncrementalValueProvider<EquatableArray<ServiceTarget>> duplicates = files
                .Select(static (file, _) => Targets(file))
                .Collect()
                .Select(static (all, _) => Duplicates(all))
                .WithTrackingName(DuplicatesStep);

            context.RegisterSourceOutput(files.Combine(enabled).Combine(duplicates), static (spc, input) =>
            {
                FileModel file = input.Left.Left;
                if (!input.Left.Right)
                {
                    return;
                }

                bool failed = false;
                foreach (DiagnosticInfo info in file.Diagnostics)
                {
                    spc.ReportDiagnostic(Diagnostics.Create(info));
                    failed = true;
                }

                foreach (ServiceTarget target in Targets(file))
                {
                    foreach (ServiceTarget other in input.Right)
                    {
                        if (other.ClassName == target.ClassName && other.ProtoFile != target.ProtoFile)
                        {
                            var detail = "the class '" + target.ClassName + "' is also generated for the service '" + other.ServiceFullName + "' in '" + other.ProtoFile + "'";
                            spc.ReportDiagnostic(Diagnostics.Create(new DiagnosticInfo(
                                Diagnostics.Collision, new EquatableArray<string>(new[] { target.ServiceFullName, detail }), file.Location)));
                            failed = true;
                        }
                    }
                }

                if (!failed && file.Services.Count > 0)
                {
                    spc.AddSource(Emitter.HintName(file), Emitter.Emit(file));
                }
            });
        }

        private static EquatableArray<ServiceTarget> Targets(FileModel file)
            => new EquatableArray<ServiceTarget>(file.Services
                .Select(s => new ServiceTarget(Emitter.ClassName(file, s), file.ProtoFile, s.FullName))
                .ToArray());

        private static EquatableArray<ServiceTarget> Duplicates(ImmutableArray<EquatableArray<ServiceTarget>> all)
            => new EquatableArray<ServiceTarget>(all
                .SelectMany(targets => targets)
                .GroupBy(t => t.ClassName, StringComparer.Ordinal)
                .Where(g => g.Select(t => t.ProtoFile).Distinct(StringComparer.Ordinal).Count() > 1)
                .SelectMany(g => g)
                .OrderBy(t => t.ClassName, StringComparer.Ordinal)
                .ThenBy(t => t.ProtoFile, StringComparer.Ordinal)
                .ToArray());

        // Syntax only: a static partial class named *Reflection with a static FileDescriptor Descriptor property and a
        // FromBase64String call, the shape protoc's C# generator emits for every file.
        internal static bool IsCandidate(SyntaxNode node)
        {
            if (node is not ClassDeclarationSyntax cls
                || !cls.Identifier.ValueText.EndsWith("Reflection", StringComparison.Ordinal)
                || !cls.Modifiers.Any(SyntaxKind.StaticKeyword)
                || !cls.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return false;
            }

            bool hasDescriptor = false;
            foreach (MemberDeclarationSyntax member in cls.Members)
            {
                if (member is PropertyDeclarationSyntax property
                    && property.Identifier.ValueText == "Descriptor"
                    && property.Modifiers.Any(SyntaxKind.StaticKeyword)
                    && LastName(property.Type) == "FileDescriptor")
                {
                    hasDescriptor = true;
                    break;
                }
            }

            return hasDescriptor && FindInvocation(cls, "FromBase64String") != null;
        }

        private static FileModel? Extract(GeneratorSyntaxContext context, CancellationToken cancellationToken)
        {
            var cls = (ClassDeclarationSyntax)context.Node;
            SemanticModel model = context.SemanticModel;
            if (model.GetDeclaredSymbol(cls, cancellationToken) is not INamedTypeSymbol reflection)
            {
                return null;
            }

            // The semantic half of the shape check: the Descriptor property must be protobuf's FileDescriptor.
            IPropertySymbol? descriptor = reflection.GetMembers("Descriptor").OfType<IPropertySymbol>().FirstOrDefault(p => p.IsStatic);
            if (descriptor == null || descriptor.Type.ToDisplayString() != FileDescriptorType)
            {
                return null;
            }

            string ns = reflection.ContainingNamespace.IsGlobalNamespace ? "" : reflection.ContainingNamespace.ToDisplayString();
            string reflectionName = reflection.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            LocationInfo? here = LocationInfo.From(cls.Identifier.GetLocation());
            try
            {
                return new Extractor(model, cls, reflection, ns, reflectionName, here, cancellationToken).Run();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(reflectionName, ns, Diagnostics.InternalError, here, reflection.Name, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static FileModel Failed(string reflectionName, string ns, string id, LocationInfo? location, params string[] arguments)
        {
            var diagnostic = new DiagnosticInfo(id, new EquatableArray<string>(arguments), location);
            return new FileModel("", ns, reflectionName, location, default, new EquatableArray<DiagnosticInfo>(new[] { diagnostic }));
        }

        private static string? LastName(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };

        private static InvocationExpressionSyntax? FindInvocation(SyntaxNode root, string name)
        {
            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? called = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => null,
                };
                if (called == name)
                {
                    return invocation;
                }
            }

            return null;
        }

        private sealed class Extractor
        {
            private readonly SemanticModel _model;
            private readonly ClassDeclarationSyntax _class;
            private readonly INamedTypeSymbol _reflection;
            private readonly string _namespace;
            private readonly string _reflectionName;
            private readonly LocationInfo? _here;
            private readonly CancellationToken _cancellationToken;
            private readonly List<DiagnosticInfo> _diagnostics = new List<DiagnosticInfo>();
            private readonly Dictionary<string, string> _localTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<string> _dependencyNamespaces = new List<string>();

            public Extractor(SemanticModel model, ClassDeclarationSyntax cls, INamedTypeSymbol reflection, string ns, string reflectionName, LocationInfo? here, CancellationToken cancellationToken)
            {
                _model = model;
                _class = cls;
                _reflection = reflection;
                _namespace = ns;
                _reflectionName = reflectionName;
                _here = here;
                _cancellationToken = cancellationToken;
            }

            public FileModel Run()
            {
                InvocationExpressionSyntax? decode = FindInvocation(_class, "FromBase64String");
                var literal = new StringBuilder();
                if (decode == null || decode.ArgumentList.Arguments.Count != 1 || !TryCollect(decode.ArgumentList.Arguments[0].Expression, literal))
                {
                    return Unreadable("the descriptor is not a constant string");
                }

                byte[] data;
                try
                {
                    data = Convert.FromBase64String(literal.ToString());
                }
                catch (FormatException)
                {
                    return Unreadable("the descriptor is not valid base64");
                }

                if (!DescriptorReader.TryRead(data, out ProtoFile file))
                {
                    return Unreadable("the descriptor is not a valid FileDescriptorProto");
                }

                string? shape = ReadGeneratedCode(file);
                if (shape != null)
                {
                    return Unreadable(shape);
                }

                var services = new List<ServiceModel>();
                foreach (ProtoService service in file.Services)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    string serviceFullName = file.Package.Length == 0 ? service.Name : file.Package + "." + service.Name;
                    var methods = new List<MethodModel>();
                    foreach (ProtoMethod method in service.Methods)
                    {
                        string methodFullName = serviceFullName + "." + method.Name;
                        string? request = Resolve(method.InputType, methodFullName);
                        string? response = Resolve(method.OutputType, methodFullName);
                        if (request == null || response == null)
                        {
                            continue;
                        }

                        CallKind kind = method.ClientStreaming
                            ? (method.ServerStreaming ? CallKind.Duplex : CallKind.ClientStreaming)
                            : (method.ServerStreaming ? CallKind.ServerStreaming : CallKind.Unary);
                        methods.Add(new MethodModel(method.Name, kind, request, response));
                    }

                    CheckCollisions(service);
                    services.Add(new ServiceModel(service.Name, serviceFullName, new EquatableArray<MethodModel>(methods.ToArray())));
                }

                return new FileModel(file.Name, _namespace, _reflectionName, _here, new EquatableArray<ServiceModel>(services.ToArray()), new EquatableArray<DiagnosticInfo>(_diagnostics.ToArray()));
            }

            private FileModel Unreadable(string reason)
                => Failed(_reflectionName, _namespace, Diagnostics.UnreadableDescriptor, _here, _reflection.Name, reason);

            private static bool TryCollect(ExpressionSyntax expression, StringBuilder into)
            {
                switch (expression)
                {
                    case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                        into.Append(literal.Token.ValueText);
                        return true;
                    case ParenthesizedExpressionSyntax parenthesized:
                        return TryCollect(parenthesized.Expression, into);
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                        return TryCollect(binary.Left, into) && TryCollect(binary.Right, into);
                    case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Concat" }:
                        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
                        {
                            if (!TryCollect(argument.Expression, into))
                            {
                                return false;
                            }
                        }

                        return true;
                    default:
                        return false;
                }
            }

            // Reads the dependency list and the message type list of FileDescriptor.FromGeneratedCode. Returns the reason the
            // shape is not understood, or null.
            private string? ReadGeneratedCode(ProtoFile file)
            {
                InvocationExpressionSyntax? build = FindInvocation(_class, "FromGeneratedCode");
                if (build == null || build.ArgumentList.Arguments.Count < 3)
                {
                    return "no FromGeneratedCode call was found";
                }

                SeparatedSyntaxList<ArgumentSyntax> arguments = build.ArgumentList.Arguments;
                List<ExpressionSyntax>? dependencies = Elements(arguments[1].Expression);
                if (dependencies == null || dependencies.Count != file.Dependencies.Count)
                {
                    return "its dependency list does not match the descriptor";
                }

                foreach (ExpressionSyntax dependency in dependencies)
                {
                    if (_model.GetSymbolInfo(dependency, _cancellationToken).Symbol is not IPropertySymbol { ContainingType: { } owner })
                    {
                        return "a dependency's descriptor could not be resolved";
                    }

                    _dependencyNamespaces.Add(owner.ContainingNamespace.IsGlobalNamespace ? "" : owner.ContainingNamespace.ToDisplayString());
                }

                if (arguments[2].Expression is not BaseObjectCreationExpressionSyntax root || root.ArgumentList == null || root.ArgumentList.Arguments.Count == 0)
                {
                    return "its type list is not understood";
                }

                string prefix = file.Package.Length == 0 ? "" : "." + file.Package;
                return MapMessages(file.Messages, Last(root.ArgumentList.Arguments), prefix) ? null : "its type list does not match the descriptor";
            }

            private bool MapMessages(List<ProtoMessage> messages, ExpressionSyntax infos, string prefix)
            {
                List<ExpressionSyntax>? elements = Elements(infos);
                if (elements == null || elements.Count != messages.Count)
                {
                    return false;
                }

                for (int i = 0; i < messages.Count; i++)
                {
                    ProtoMessage message = messages[i];
                    string fullName = prefix + "." + message.Name;
                    ExpressionSyntax element = elements[i];
                    if (element.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        // protoc emits no C# type for a map entry.
                        if (!message.MapEntry)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (element is not BaseObjectCreationExpressionSyntax info
                        || info.ArgumentList == null
                        || info.ArgumentList.Arguments.Count < 2
                        || info.ArgumentList.Arguments[0].Expression is not TypeOfExpressionSyntax typeOf
                        || _model.GetTypeInfo(typeOf.Type, _cancellationToken).Type is not INamedTypeSymbol type
                        || type.TypeKind == TypeKind.Error)
                    {
                        return false;
                    }

                    _localTypes[fullName] = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (!MapMessages(message.Nested, Last(info.ArgumentList.Arguments), fullName))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static ExpressionSyntax Last(SeparatedSyntaxList<ArgumentSyntax> arguments) => arguments[arguments.Count - 1].Expression;

            // The elements of an array creation; an empty list for a null literal; null for anything else.
            private static List<ExpressionSyntax>? Elements(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression):
                        return new List<ExpressionSyntax>();
                    case ArrayCreationExpressionSyntax array:
                        return array.Initializer == null ? new List<ExpressionSyntax>() : array.Initializer.Expressions.ToList();
                    case ImplicitArrayCreationExpressionSyntax implicitArray:
                        return implicitArray.Initializer.Expressions.ToList();
                    default:
                        return null;
                }
            }

            // A message of this file maps through protoc's own type list. An imported one is looked up in each dependency's C#
            // namespace, trying every split of its full name into package and nested type path, and must be a protobuf
            // message; exactly one match is accepted.
            private string? Resolve(string protoType, string methodFullName)
            {
                if (_localTypes.TryGetValue(protoType, out string? local))
                {
                    return local;
                }

                string[] segments = protoType.TrimStart('.').Split('.');
                var matches = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (string ns in _dependencyNamespaces.Distinct())
                {
                    for (int packageSegments = 0; packageSegments < segments.Length; packageSegments++)
                    {
                        string path = string.Join("+Types+", segments, packageSegments, segments.Length - packageSegments);
                        string metadataName = ns.Length == 0 ? path : ns + "." + path;
                        INamedTypeSymbol? candidate = _model.Compilation.GetTypeByMetadataName(metadataName);
                        if (candidate != null && IsMessage(candidate))
                        {
                            matches.Add(candidate);
                        }
                    }
                }

                if (matches.Count == 1)
                {
                    return matches.First().ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }

                _diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.UnresolvedType,
                    new EquatableArray<string>(new[] { methodFullName, protoType, matches.Count == 0 ? "no C# type was found" : "more than one C# type matches" }),
                    _here));
                return null;
            }

            private static bool IsMessage(INamedTypeSymbol type)
            {
                foreach (INamedTypeSymbol implemented in type.AllInterfaces)
                {
                    if (implemented.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Google.Protobuf.IMessage<T>"
                        && SymbolEqualityComparer.Default.Equals(implemented.TypeArguments[0], type))
                    {
                        return true;
                    }
                }

                return false;
            }

            private void CheckCollisions(ProtoService service)
            {
                string target = _namespace.Length == 0 ? service.Name : _namespace + "." + service.Name;
                var names = new List<string> { Emitter.BinderName, Emitter.TagsName };
                if (service.Name == Emitter.BinderName)
                {
                    Collision(service, "the service name '" + Emitter.BinderName + "' is the name of its own generated binder", null);
                }
                else if (service.Name == Emitter.TagsName)
                {
                    Collision(service, "the service name '" + Emitter.TagsName + "' is the name of its own generated method tags", null);
                }

                foreach (ProtoMethod method in service.Methods)
                {
                    if (method.Name == service.Name)
                    {
                        Collision(service, "the method '" + method.Name + "' has the name of its service class", null);
                    }
                    else if (method.Name == Emitter.BinderName)
                    {
                        Collision(service, "the method name '" + Emitter.BinderName + "' is reserved for the generated binder", null);
                    }
                    else if (method.Name == Emitter.TagsName)
                    {
                        Collision(service, "the method name '" + Emitter.TagsName + "' is reserved for the generated method tags", null);
                    }

                    names.Add(method.Name);
                }

                INamedTypeSymbol? existing = _model.Compilation.GetTypeByMetadataName(target);
                if (existing == null)
                {
                    return;
                }

                Location? location = existing.Locations.FirstOrDefault(l => l.IsInSource);
                bool isPartial = existing.DeclaringSyntaxReferences.Length > 0
                    && existing.DeclaringSyntaxReferences.All(r => r.GetSyntax(_cancellationToken) is TypeDeclarationSyntax t && t.Modifiers.Any(SyntaxKind.PartialKeyword));
                if (existing.TypeKind != TypeKind.Class || !existing.IsStatic || !isPartial)
                {
                    Collision(service, "'" + target + "' already exists and is not a static partial class", location);
                    return;
                }

                foreach (string name in names)
                {
                    if (!existing.GetMembers(name).IsEmpty)
                    {
                        Collision(service, "'" + target + "' already has a member named '" + name + "'", location);
                    }
                }
            }

            private void Collision(ProtoService service, string detail, Location? location)
            {
                _diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.Collision,
                    new EquatableArray<string>(new[] { service.Name, detail }),
                    LocationInfo.From(location) ?? _here));
            }
        }
    }
}
