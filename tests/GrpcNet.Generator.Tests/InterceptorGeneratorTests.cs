using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace GrpcNet.Generator.Tests
{
    public class InterceptorGeneratorTests
    {
        private const string Usings = "using System; using System.Threading; using System.Threading.Tasks; using GrpcNet; using GrpcNet.Protobuf; using GrpcNet.Generator.Tests.Calc; ";

        private const string Impl = "public class Impl { "
            + "public AddReply Add(AddRequest r) => new AddReply(); "
            + "public ValueTask<AddReply> AddAsync(AddRequest r, CancellationToken ct) => default; "
            + "public ValueTask<AddReply> AddTokenless(AddRequest r) => default; "
            + "public ValueTask Count(AddRequest r, MessageWriter<AddReply> w, CancellationToken ct) => default; "
            + "public ValueTask<AddReply> Sum(MessageReader<AddRequest> r, CancellationToken ct) => default; "
            + "public ValueTask Chat(MessageReader<Outer.Types.Inner> r, MessageWriter<Outer.Types.Inner> w, CancellationToken ct) => default; "
            + "public static AddReply StaticAdd(AddRequest r) => new AddReply(); "
            + "public AddReply Over(AddRequest r) => new AddReply(); public AddReply Over(AddRequest r, int x) => new AddReply(); "
            + "protected internal AddReply Shared(AddRequest r) => new AddReply(); "
            + "public AddReply Loose(Google.Protobuf.IMessage r) => new AddReply(); } "
            + "public class Base { public virtual AddReply Add(AddRequest r) => new AddReply(); } "
            + "public sealed class Derived : Base { public override AddReply Add(AddRequest r) => new AddReply(); } "
            + "public struct Value { public AddReply Add(AddRequest r) => new AddReply(); } "
            + "public static class Ext { public static AddReply Twice(this Impl i, AddRequest r) => new AddReply(); } "
            + "public static class Gen { public static AddReply Add<T>(AddRequest r) => new AddReply(); } ";

        // Declarations whose own bodies hold the call under test, for cases a statement in User.M cannot express.
        private const string Private = "public sealed class P { private AddReply Hidden(AddRequest r) => new AddReply(); public void Go(GrpcProcessorBuilder b) => b.Bind(Calculator.Add, Hidden); } ";
        private const string ViaBase = "public sealed class D : Base { public override AddReply Add(AddRequest r) => new AddReply(); public void Go(GrpcProcessorBuilder b) => b.Bind(Calculator.Add, base.Add); } ";
        private const string OpenHost = "public sealed class H<T> { public AddReply Add(AddRequest r) => new AddReply(); public void Go(GrpcProcessorBuilder b) => b.Bind(Calculator.Add, Add); } ";
        private const string ClosedHost = "public sealed class H<T> { public void Go(GrpcProcessorBuilder b) => b.Bind(Calculator.Add, Impl.StaticAdd); } ";
        private const string GenericMethod = "public static class H { public static void Go<T>(GrpcProcessorBuilder b) => b.Bind(Calculator.Add, Impl.StaticAdd); } ";
        private const string Manual = "public static class Manual { public static readonly UnaryMethod<AddRequest, AddReply> Add = new(CalcReflection.Descriptor, \"calc.Calculator\", \"Add\"); } ";
        private const string CloserBind = "namespace Near { public static class Mine { public static GrpcProcessorBuilder Bind<TQ, TR>(this GrpcProcessorBuilder b, UnaryMethod<TQ, TR> m, Func<TQ, TR> h) "
            + "where TQ : class, Google.Protobuf.IMessage<TQ> where TR : class, Google.Protobuf.IMessage<TR> => b; } "
            + "public static class Caller { public static void Go(GrpcProcessorBuilder b, Impl i) => b.Bind(Calculator.Add, i.Add); } } ";
        private const string Interface = "public interface IAdd { AddReply Add(AddRequest r); } "
            + "public sealed class AddImpl : IAdd { AddReply IAdd.Add(AddRequest r) => new AddReply(); } "
            + "public static class IH { public static void Go(GrpcProcessorBuilder b, IAdd a) => b.Bind(Calculator.Add, a.Add); } ";
        private const string FileLocal = "file sealed class L { public AddReply Add(AddRequest r) => new AddReply(); public static void Go(GrpcProcessorBuilder b) => b.Bind(Calculator.Add, new L().Add); } ";

        private static Harness.Result Run(string body, bool optIn = true, string extra = "")
        {
            string user = Usings + Impl + extra + "class User { void M(GrpcProcessorBuilder b, Impl i, Base bs, Value v, Func<AddRequest, AddReply> f) { " + body + " } }";
            return Harness.Run(Harness.Calc().Append(user), options: optIn ? Harness.OptedIn : null);
        }

        private static int Intercepted(Harness.Result result)
            => result.HintNames.Contains(Interceptors.HintName) ? Regex.Matches(result.Generated(Interceptors.HintName), "InterceptsLocation\\(").Count : 0;

        private static void AssertCompiles(Harness.Result result)
            => Assert.That(result.CompileProblems(), Is.Empty, string.Join("\n", result.CompileProblems().Select(d => d.ToString())));

        [TestCase("b.Bind(Calculator.Add, i.Add);", TestName = "Bind, instance method")]
        [TestCase("b.Bind(Calculator.Add, Impl.StaticAdd);", TestName = "Bind, static method")]
        [TestCase("ProtobufBindings.Bind(b, Calculator.Add, i.Add);", TestName = "Bind, static call form")]
        [TestCase("b.BindAsync(Calculator.Add, i.AddAsync);", TestName = "BindAsync with the token")]
        [TestCase("b.BindAsync(Calculator.Add, i.AddTokenless);", TestName = "BindAsync without the token")]
        [TestCase("b.Bind(Calculator.Count, i.Count);", TestName = "Bind, server streaming")]
        [TestCase("b.Bind(Calculator.Sum, i.Sum);", TestName = "Bind, client streaming")]
        [TestCase("b.Bind(Calculator.Chat, i.Chat);", TestName = "Bind, duplex")]
        [TestCase("Calculator.Add.Sync(i.Add);", TestName = "Sync factory")]
        [TestCase("Calculator.Add.Async(i.AddAsync);", TestName = "Async factory with the token")]
        [TestCase("Calculator.Add.Async(i.AddTokenless);", TestName = "Async factory without the token")]
        [TestCase("b.Bind(Calculator.Add, bs.Add);", TestName = "virtual method on a base-typed receiver")]
        [TestCase("IH.Go(b, new AddImpl());", Interface, TestName = "an interface method on an interface-typed receiver")]
        [TestCase("b.Bind(Calculator.Add, i.Shared);", TestName = "protected internal method")]
        [TestCase("b.Bind(Manual.Add, i.Add);", Manual, TestName = "a method object written by hand")]
        [TestCase("Manual.Add.Sync(i.Add);", Manual, TestName = "Sync on a method object written by hand")]
        [TestCase("H.Go<int>(b);", GenericMethod, TestName = "closed call inside a generic method")]
        [TestCase("new H<int>().Go(b);", ClosedHost, TestName = "closed call inside a generic type")]
        public void EligibleSitesAreInterceptedAndCompile(string statement, string extra = "")
        {
            Harness.Result result = Run(statement, extra: extra);
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(Intercepted(result), Is.EqualTo(1));
            AssertCompiles(result);
        }

        [TestCase("b.Bind(Calculator.Add, r => i.Add(r));", TestName = "a lambda")]
        [TestCase("b.Bind(Calculator.Add, f);", TestName = "a delegate variable")]
        [TestCase("b.Bind(Calculator.Add, i.Over);", TestName = "an overloaded method")]
        [TestCase("new P().Go(b);", Private, TestName = "a private method")]
        [TestCase("new D().Go(b);", ViaBase, TestName = "a base. method group")]
        [TestCase("b.Bind(Calculator.Add, v.Add);", TestName = "a struct receiver")]
        [TestCase("new H<int>().Go(b);", OpenHost, TestName = "a receiver typed with a type parameter")]
        [TestCase("L.Go(b);", FileLocal, TestName = "a file-local type")]
        [TestCase("b.Bind(Calculator.Add, i.Twice);", TestName = "an extension method group")]
        [TestCase("b.Bind(Calculator.Add, Gen.Add<int>);", TestName = "a generic method")]
        [TestCase("b.Bind(Calculator.Add, new Func<AddRequest, AddReply>(i.Add));", TestName = "an explicit delegate creation")]
        [TestCase("b.Bind(Calculator.Add, i.Loose);", TestName = "a handler with a wider parameter type")]
        [TestCase("b.Bind(method: Calculator.Add, handler: i.Add);", TestName = "named arguments")]
        [TestCase("Near.Caller.Go(b, i);", CloserBind, TestName = "a closer Bind extension the call binds to")]
        public void IneligibleSitesKeepTheirDelegate(string statement, string extra = "")
        {
            Harness.Result result = Run(statement, extra: extra);
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(Intercepted(result), Is.EqualTo(0));
            AssertCompiles(result);
        }

        [Test]
        public void WithoutTheOptInNothingIsInterceptedAndGN0004SaysWhy()
        {
            Harness.Result result = Run("b.Bind(Calculator.Add, i.Add); b.Bind(Calculator.Count, i.Count);", optIn: false);
            Assert.That(result.HintNames, Has.None.EqualTo(Interceptors.HintName));
            Diagnostic diagnostic = result.GeneratorDiagnostics.Single();
            Assert.That((diagnostic.Id, diagnostic.Severity), Is.EqualTo(("GN0004", DiagnosticSeverity.Info)));
            Assert.That(diagnostic.GetMessage(), Does.Contain("2 handler(s)").And.Contain("InterceptorsNamespaces"));
            Assert.That(diagnostic.Location.GetLineSpan().Path, Is.EqualTo("Source2.cs"));
            AssertCompiles(result);
        }

        [Test]
        public void NoMethodGroupsMeansNoFileAndNoDiagnostic()
        {
            Harness.Result result = Run("b.Bind(Calculator.Add, r => new AddReply());", optIn: false);
            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.HintNames, Has.None.EqualTo(Interceptors.HintName));
        }

        [Test]
        public void AnUnrelatedEditReusesTheSitesAndTheInterceptorsFile()
        {
            string user = Usings + Impl + "class User { void M(GrpcProcessorBuilder b, Impl i) { b.Bind(Calculator.Add, i.Add); } }";
            CSharpCompilation input = Harness.Compilation(Harness.Calc().Append(user).Append("class Other { }"), options: Harness.OptedIn);
            GeneratorDriver driver = Harness.Driver(track: true, options: Harness.OptedIn).RunGenerators(input);
            Assert.That(driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.HintName), Has.Member(Interceptors.HintName));

            SyntaxTree other = input.SyntaxTrees.Last();
            CSharpCompilation edited = input.ReplaceSyntaxTree(other, CSharpSyntaxTree.ParseText("class Other { int x; }", Harness.OptedIn, path: other.FilePath));
            GeneratorRunResult run = driver.RunGenerators(edited).GetRunResult().Results.Single();

            IncrementalGeneratorRunStep[] sites = run.TrackedSteps[GrpcNetGenerator.SitesStep].ToArray();
            Assert.That(sites.SelectMany(s => s.Outputs), Is.Not.Empty);
            Assert.That(sites.SelectMany(s => s.Outputs).Select(o => o.Reason), Is.All.EqualTo(IncrementalStepRunReason.Cached).Or.EqualTo(IncrementalStepRunReason.Unchanged));
            Assert.That(run.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs).Select(o => o.Reason), Is.All.EqualTo(IncrementalStepRunReason.Cached));
        }

        [Test]
        public void TheOptInIsReadFromEitherProperty()
        {
            Assert.That(Interceptors.OptedIn(Harness.ParseOptions.WithFeatures(new[] { new System.Collections.Generic.KeyValuePair<string, string>("InterceptorsPreviewNamespaces", "A;GrpcNet.Generated") })), Is.True);
            Assert.That(Interceptors.OptedIn(Harness.ParseOptions.WithFeatures(new[] { new System.Collections.Generic.KeyValuePair<string, string>("InterceptorsNamespaces", "GrpcNet.GeneratedX") })), Is.False);
            Assert.That(Interceptors.OptedIn(Harness.ParseOptions), Is.False);
        }
    }
}
