using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace GrpcNet.Generator.Tests
{
    public class GeneratorTests
    {
        private const string CalcHint = "GrpcNet.Generator.Tests.Calc.CalcReflection.GrpcNet.g.cs";

        private static string Directory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

        [Test]
        public void EmitsOneFilePerProtoWithServicesAndNothingElse()
        {
            Harness.Result result = Harness.Run(Harness.Calc().Append(Harness.Protoc("Nopkg.cs")).ToArray());
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.HintNames, Is.EquivalentTo(new[] { CalcHint, "NopkgReflection.GrpcNet.g.cs" }), "common.proto declares no service");
            Assert.That(result.CompileProblems(), Is.Empty);
        }

        [Test]
        public void CalcOutputMatchesTheSnapshot()
        {
            string generated = Harness.Run(Harness.Calc()).Generated(CalcHint);
            string snapshot = Path.Combine(Directory(), "Snapshots", "Calc.g.cs.txt");
            string expected = File.Exists(snapshot) ? File.ReadAllText(snapshot).Replace("\r\n", "\n") : "";
            if (generated != expected)
            {
                File.WriteAllText(snapshot + ".received", generated);
                Assert.Fail("The generated code differs from " + snapshot + "; the actual output is in the .received file next to it.");
            }
        }

        [Test]
        public void KeywordAndObjectMemberNamesCompileWithoutWarnings()
        {
            Harness.Result result = Harness.Run(Harness.Protoc("Quirks.cs"));
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.CompileProblems(), Is.Empty, string.Join("\n", result.CompileProblems().Select(d => d.ToString())));
            string generated = result.Generated("GrpcNet.Generator.Tests.Quirks.QuirksReflection.GrpcNet.g.cs");
            Assert.That(generated, Does.Contain(" @event ").And.Contain("readonly new global::GrpcNet.Protobuf.UnaryMethod"));
            Assert.That(generated, Does.Contain("public static partial class @event").And.Contain("global::GrpcNet.Generator.Tests.Quirks.@event.Go"));
        }

        [Test]
        public void MethodNamedLikeItsServiceOrTheBinderIsReported()
        {
            Harness.Result result = Harness.Run(Harness.Protoc("Collide.cs"));
            Assert.That(result.GeneratorDiagnostics.Select(d => d.Id), Is.EqualTo(new[] { "GN0003", "GN0003", "GN0003" }));
            Assert.That(result.HintNames, Is.Empty, "a file with an error emits nothing");
            string[] messages = result.GeneratorDiagnostics.Select(d => d.GetMessage()).ToArray();
            Assert.That(messages, Has.Some.Contains("the method 'Echo' has the name of its service class"));
            Assert.That(messages, Has.Some.Contains("the method name 'Binder' is reserved"));
            Assert.That(messages, Has.Some.Contains("the service name 'Binder' is the name of its own generated binder"));
            Assert.That(result.GeneratorDiagnostics.All(d => d.Severity == DiagnosticSeverity.Error), Is.True);
        }

        private static IEnumerable<TestCaseData> Collisions()
        {
            yield return new TestCaseData("namespace GrpcNet.Generator.Tests.Calc { public class Calculator { } }", "is not a static partial class").SetName("a non-static class");
            yield return new TestCaseData("namespace GrpcNet.Generator.Tests.Calc { public static class Calculator { } }", "is not a static partial class").SetName("a non-partial static class");
            yield return new TestCaseData("namespace GrpcNet.Generator.Tests.Calc { public struct Calculator { } }", "is not a static partial class").SetName("a struct");
            yield return new TestCaseData("namespace GrpcNet.Generator.Tests.Calc { public static partial class Calculator { public static int Add; } }", "already has a member named 'Add'").SetName("a member named like a method");
            yield return new TestCaseData("namespace GrpcNet.Generator.Tests.Calc { public static partial class Calculator { public sealed class Binder { } } }", "already has a member named 'Binder'").SetName("a member named Binder");
        }

        [TestCaseSource(nameof(Collisions))]
        public void CollisionWithExistingCodeIsReportedNotCompiledInto(string user, string detail)
        {
            Harness.Result result = Harness.Run(Harness.Calc().Append(user).ToArray());
            Diagnostic diagnostic = result.GeneratorDiagnostics.Single();
            Assert.That(diagnostic.Id, Is.EqualTo("GN0003"));
            Assert.That(diagnostic.GetMessage(), Does.Contain(detail));
            Assert.That(diagnostic.Location.GetLineSpan().Path, Is.EqualTo("Source2.cs"), "reported at the colliding type");
            Assert.That(result.HintNames, Is.Empty);
        }

        [Test]
        public void SameClassFromTwoFilesIsReportedOnBothAndEmittedFromNeither()
        {
            Harness.Result result = Harness.Run(Harness.Protoc("DupA.cs"), Harness.Protoc("DupB.cs"));
            Diagnostic[] diagnostics = result.GeneratorDiagnostics.OrderBy(d => d.Location.GetLineSpan().Path, StringComparer.Ordinal).ToArray();
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "GN0003", "GN0003" }));
            Assert.That(diagnostics.Select(d => d.Location.GetLineSpan().Path), Is.EqualTo(new[] { "Source0.cs", "Source1.cs" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("'GrpcNet.Generator.Tests.Dup.S' is also generated for the service 'dup.b.S' in 'dup_b.proto'"));
            Assert.That(diagnostics[1].GetMessage(), Does.Contain("'GrpcNet.Generator.Tests.Dup.S' is also generated for the service 'dup.a.S' in 'dup_a.proto'"));
            Assert.That(result.HintNames, Is.Empty);
            Assert.That(result.CompileProblems(), Is.Empty);

            // Either file alone is fine.
            Harness.Result alone = Harness.Run(Harness.Protoc("DupA.cs"));
            Assert.That(alone.GeneratorDiagnostics, Is.Empty);
            Assert.That(alone.CompileProblems(), Is.Empty);
        }

        [Test]
        public void AnExistingStaticPartialClassIsMergedWith()
        {
            string user = "namespace GrpcNet.Generator.Tests.Calc { public static partial class Calculator { public static int Other; } }";
            Harness.Result result = Harness.Run(Harness.Calc().Append(user).ToArray());
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.CompileProblems(), Is.Empty);
        }

        private static IEnumerable<TestCaseData> Misuse()
        {
            yield return new TestCaseData(
                "b.Bind(new Calculator.Binder { Add = r => new AddReply() });",
                new[] { "CS9035", "CS9035", "CS9035", "CS9035", "CS9035" }).SetName("a binder missing handlers");
            yield return new TestCaseData(
                "b.Bind(Calculator.Add, (AddReply r) => new AddReply());",
                new[] { "CS0411" }).SetName("a handler with the wrong request type");
            yield return new TestCaseData(
                "b.Bind(Calculator.Add, (AddRequest r) => new AddRequest());",
                new[] { "CS0411" }).SetName("a handler with the wrong response type");
            yield return new TestCaseData(
                "b.Bind(Calculator.Add, async r => { await System.Threading.Tasks.Task.Yield(); return new AddReply(); });",
                new[] { "CS0411" }).SetName("an async handler bound with Bind");
            yield return new TestCaseData(
                "b.BindAsync(Calculator.Add, async r => { await System.Threading.Tasks.Task.Yield(); return new AddRequest(); });",
                new[] { "CS1593" }).SetName("an async handler with the wrong response type");
            yield return new TestCaseData(
                "b.BindAsync(Calculator.Count, async r => { await System.Threading.Tasks.Task.Yield(); return new AddReply(); });",
                new[] { "CS0411" }).SetName("BindAsync on a server-streaming method");
            yield return new TestCaseData(
                "b.Bind(Calculator.Count, (AddRequest r) => new AddReply());",
                new[] { "CS1503" }).SetName("a unary handler for a server-streaming method");
            yield return new TestCaseData(
                "b.Bind(new Calculator.Binder { Add = r => new AddReply(), Count = (r, w, ct) => default, Sum = (rs, ct) => default, Chat = (rs, ws, ct) => default, Ping = r => r, Share = r => new AddReply() });",
                new[] { "CS0029", "CS1662" }).SetName("a binder handler with the wrong response type");
        }

        [TestCaseSource(nameof(Misuse))]
        public void MisuseDoesNotCompile(string statement, string[] expected)
        {
            string user = "using GrpcNet.Generator.Tests.Calc; using GrpcNet.Protobuf; class User { void M(GrpcNet.GrpcProcessorBuilder b) { " + statement + " } }";
            Harness.Result result = Harness.Run(Harness.Calc().Append(user).ToArray());
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            string[] errors = result.CompileProblems().Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Assert.That(errors, Is.EqualTo(expected), string.Join("\n", result.CompileProblems().Select(d => d.ToString())));
        }

        [Test]
        public void CorrectUseCompiles()
        {
            string user = "using GrpcNet.Generator.Tests.Calc; using GrpcNet.Protobuf; class User { void M(GrpcNet.GrpcProcessorBuilder b) { "
                + "b.Bind(new Calculator.Binder { Add = r => new AddReply { Sum = r.A + r.B }, Count = (r, w, ct) => default, Sum = (rs, ct) => default, "
                + "Chat = async (rs, ws, ct) => { while (await rs.MoveNextAsync()) await ws.WriteAsync(rs.Current); }, Ping = r => r, Share = r => new Common.V1.Shared() }); "
                + "b.Bind(new Second.Binder { Describe = o => o.Inner }); "
                + "b.BindAsync(Calculator.Add, async r => { await System.Threading.Tasks.Task.Yield(); return new AddReply(); }); "
                + "b.BindAsync(Calculator.Add, async (r, ct) => { await System.Threading.Tasks.Task.Delay(1, ct); return new AddReply(); }); } }";
            Harness.Result result = Harness.Run(Harness.Calc().Append(user).ToArray());
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.CompileProblems(), Is.Empty, string.Join("\n", result.CompileProblems().Select(d => d.ToString())));
        }

        [Test]
        public void CorruptDescriptorIsReported()
        {
            string calc = Harness.Protoc("Calc.cs");
            int start = calc.IndexOf("string.Concat(", StringComparison.Ordinal);
            int quote = calc.IndexOf('"', start) + 1;
            string corrupt = calc.Substring(0, quote) + "!!" + calc.Substring(quote + 2);
            Harness.Result result = Harness.Run(corrupt, Harness.Protoc("Common.cs"));
            Diagnostic diagnostic = result.GeneratorDiagnostics.Single();
            Assert.That(diagnostic.Id, Is.EqualTo("GN0001"));
            Assert.That(diagnostic.GetMessage(), Does.Contain("not valid base64"));
        }

        [Test]
        public void NonConstantDescriptorIsReported()
        {
            string calc = Harness.Protoc("Calc.cs");
            int start = calc.IndexOf("string.Concat(", StringComparison.Ordinal);
            string changed = calc.Substring(0, start) + "Pieces() + " + calc.Substring(start);
            changed = changed.Replace("static CalcReflection() {", "private static string Pieces() => \"\";\n    static CalcReflection() {");
            Harness.Result result = Harness.Run(changed, Harness.Protoc("Common.cs"));
            Assert.That(result.GeneratorDiagnostics.Single().Id, Is.EqualTo("GN0001"));
            Assert.That(result.GeneratorDiagnostics.Single().GetMessage(), Does.Contain("not a constant string"));
        }

        [Test]
        public void AnUnrelatedReflectionClassIsIgnored()
        {
            string unrelated = "public static partial class ThingReflection { public static System.Type Descriptor => typeof(int); static byte[] Data = System.Convert.FromBase64String(\"!!\"); }";
            string lookalike = "namespace Fake { public sealed class FileDescriptor { } } public static partial class OtherReflection { public static Fake.FileDescriptor Descriptor => null!; static byte[] Data = System.Convert.FromBase64String(\"!!\"); }";
            Harness.Result result = Harness.Run(unrelated, lookalike);
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.HintNames, Is.Empty);
        }

        [Test]
        public void UnresolvableImportedTypeIsReported()
        {
            // The import's reflection class exists, so the dependency resolves, but its messages do not.
            string stub = "namespace Common.V1 { public static partial class CommonReflection { public static Google.Protobuf.Reflection.FileDescriptor Descriptor => null!; } }";
            Harness.Result result = Harness.Run(Harness.Protoc("Calc.cs"), stub);
            Diagnostic[] diagnostics = result.GeneratorDiagnostics.ToArray();
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "GN0002", "GN0002" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("'.common.v1.Shared.Item'").And.Contain("calc.Calculator.Share"));
            Assert.That(result.HintNames, Is.Empty);
        }

        [Test]
        public void NothingIsGeneratedWithoutGrpcNetReferenced()
        {
            Harness.Result result = Harness.Run(Harness.Calc(), withGrpcNet: false);
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.HintNames, Is.Empty);
        }

        [Test]
        public void UnrelatedEditsReuseTheCachedModel()
        {
            CSharpCompilation input = Harness.Compilation(Harness.Calc().Append("class User { }"));
            GeneratorDriver driver = Harness.Driver(track: true).RunGenerators(input);

            CSharpCompilation edited = input.ReplaceSyntaxTree(
                input.SyntaxTrees.Last(),
                CSharpSyntaxTree.ParseText("class User { int x; }", Harness.ParseOptions, path: input.SyntaxTrees.Last().FilePath));
            GeneratorRunResult run = driver.RunGenerators(edited).GetRunResult().Results.Single();

            IncrementalGeneratorRunStep[] models = run.TrackedSteps[GrpcNetGenerator.FileModelsStep].ToArray();
            Assert.That(models, Is.Not.Empty);
            Assert.That(models.SelectMany(s => s.Outputs).Select(o => o.Reason), Is.All.EqualTo(IncrementalStepRunReason.Cached).Or.EqualTo(IncrementalStepRunReason.Unchanged));
            Assert.That(run.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs).Select(o => o.Reason), Is.All.EqualTo(IncrementalStepRunReason.Cached));
        }
    }
}
