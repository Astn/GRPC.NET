using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace GrpcNet.Generator.Tests
{
    /// <summary>Compiles sources in memory and runs the generator over them, as the compiler would.</summary>
    internal static class Harness
    {
        public static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        /// <summary>The parse options of a project that lists GrpcNet.Generated in InterceptorsNamespaces, as the Csc task passes it.</summary>
        public static readonly CSharpParseOptions OptedIn = ParseOptions.WithFeatures(new[] { new KeyValuePair<string, string>("InterceptorsNamespaces", ";GrpcNet.Generated;") });

        private static readonly Lazy<MetadataReference[]> s_references = new Lazy<MetadataReference[]>(() =>
        {
            // The framework and the libraries generated code uses, but not this test assembly, which holds its own compiled copy
            // of the protoc output the tests feed in as source.
            string[] excluded = { "GrpcNet.Generator.Tests.dll", "GrpcNet.Generator.dll" };
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => !excluded.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
            return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
        });

        /// <summary>protoc's C# output for one of the test protos, as this project's build produced it.</summary>
        public static string Protoc(string fileName) => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "protoc", fileName));

        public static CSharpCompilation Compilation(IEnumerable<string> sources, bool withGrpcNet = true, CSharpParseOptions? options = null)
        {
            IEnumerable<MetadataReference> references = s_references.Value;
            if (!withGrpcNet)
            {
                references = references.Where(r => !Path.GetFileName(((PortableExecutableReference)r).FilePath!).StartsWith("GrpcNet.", StringComparison.Ordinal));
            }

            return CSharpCompilation.Create(
                "Fixture",
                sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, options ?? ParseOptions, path: "Source" + i + ".cs")),
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        }

        public static GeneratorDriver Driver(bool track = false, CSharpParseOptions? options = null)
            => CSharpGeneratorDriver.Create(
                new[] { new GrpcNetGenerator().AsSourceGenerator() },
                parseOptions: options ?? ParseOptions,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: track));

        public static Result Run(params string[] sources) => Run((IEnumerable<string>)sources);

        public static Result Run(IEnumerable<string> sources, bool withGrpcNet = true, CSharpParseOptions? options = null)
        {
            CSharpCompilation input = Compilation(sources, withGrpcNet, options);
            GeneratorDriver driver = Driver(options: options).RunGeneratorsAndUpdateCompilation(input, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
            return new Result(driver.GetRunResult(), output, diagnostics);
        }

        /// <summary>protoc's output for calc.proto and the file it imports.</summary>
        public static string[] Calc() => new[] { Protoc("Calc.cs"), Protoc("Common.cs") };

        internal sealed class Result
        {
            public Result(GeneratorDriverRunResult run, Compilation output, ImmutableArray<Diagnostic> diagnostics)
            {
                Run = run;
                Output = output;
                GeneratorDiagnostics = diagnostics;
            }

            public GeneratorDriverRunResult Run { get; }

            public Compilation Output { get; }

            public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; }

            public string Generated(string hintName) => Run.Results.Single().GeneratedSources.Single(s => s.HintName == hintName).SourceText.ToString();

            public IEnumerable<string> HintNames => Run.Results.Single().GeneratedSources.Select(s => s.HintName);

            /// <summary>Compiler diagnostics of warning severity or worse, from user and generated code alike.</summary>
            public Diagnostic[] CompileProblems() => Output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
        }
    }
}
