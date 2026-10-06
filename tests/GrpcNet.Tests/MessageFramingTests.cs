using System;
using System.Buffers;
using System.Collections.Generic;
using NUnit.Framework;

namespace GrpcNet.Tests
{
    public class MessageFramingTests
    {
        [Test]
        public void PrefixRoundTrips()
        {
            Span<byte> prefix = stackalloc byte[5];
            MessageFraming.WritePrefix(prefix, 0x01020304);
            Assert.That(prefix.ToArray(), Is.EqualTo(new byte[] { 0, 1, 2, 3, 4 }));
            Assert.That(MessageFraming.TryReadPrefix(prefix, out bool compressed, out uint length), Is.True);
            Assert.That(compressed, Is.False);
            Assert.That(length, Is.EqualTo(0x01020304u));
        }

        [Test]
        public void ShortInputHasNoPrefix([Range(0, 4)] int count)
        {
            Assert.That(MessageFraming.TryReadPrefix(new byte[count], out _, out _), Is.False);
            Assert.That(MessageFraming.TryReadPrefix(new ReadOnlySequence<byte>(new byte[count]), out _, out _), Is.False);
        }

        [Test]
        public void PrefixStraddlingSegmentsIsRead([Range(1, 4)] int split)
        {
            byte[] frame = Frames.Frame(new byte[300]);
            frame[0] = 1;
            ReadOnlySequence<byte> sequence = Frames.Split(frame, split);
            Assert.That(sequence.IsSingleSegment, Is.False);
            Assert.That(MessageFraming.TryReadPrefix(in sequence, out bool compressed, out uint length), Is.True);
            Assert.That(compressed, Is.True);
            Assert.That(length, Is.EqualTo(300u));
        }

        [Test]
        public void PrefixSplitIntoSingleBytesIsRead()
        {
            byte[] frame = Frames.Frame(new byte[70000]);
            ReadOnlySequence<byte> sequence = Frames.Split(frame, 1, 2, 3, 4);
            Assert.That(MessageFraming.TryReadPrefix(in sequence, out _, out uint length), Is.True);
            Assert.That(length, Is.EqualTo(70000u));
        }
    }

    public class MethodTableTests
    {
        private static GrpcProcessorBuilder Builder(params string[] paths)
        {
            var builder = new GrpcProcessorBuilder();
            foreach (string path in paths)
            {
                builder.AddUnary<int, int, IntCodec, IntCodec>(path, default, default, x => x);
            }

            return builder;
        }

        [Test]
        public void ResolvesKnownPathsAndMissesUnknownOnes()
        {
            GrpcProcessor processor = Builder("/a.B/C", "/a.B/D", "/x.Y/Z").Build();
            MethodId c = processor.Resolve("/a.B/C"u8);
            MethodId d = processor.Resolve("/a.B/D"u8);
            Assert.That(c.IsValid && d.IsValid, Is.True);
            Assert.That(c, Is.Not.EqualTo(d));
            Assert.That(processor.Resolve("/a.B/E"u8).IsValid, Is.False);
            Assert.That(processor.Resolve("/a.B/C/"u8).IsValid, Is.False);
            Assert.That(processor.Resolve(ReadOnlySpan<byte>.Empty).IsValid, Is.False);
        }

        [Test]
        public void ResolvesWhenEveryPathHashesTheSame()
        {
            GrpcProcessor processor = Builder("/a.B/C", "/a.B/D", "/a.B/E", "/a.B/F", "/a.B/G").Build(_ => 42);
            foreach (string path in new[] { "/a.B/C", "/a.B/D", "/a.B/E", "/a.B/F", "/a.B/G" })
            {
                MethodId id = processor.Resolve(System.Text.Encoding.UTF8.GetBytes(path));
                Assert.That(id.IsValid, Is.True, path);
                var sink = new RecordingSink();
                var output = new ArrayBufferWriter<byte>();
                Assert.That(processor.Process(new CallHead(id), Frames.IntFrame(7), output, ref sink).Code, Is.EqualTo(StatusCode.OK));
            }

            Assert.That(processor.Resolve("/a.B/H"u8).IsValid, Is.False);
        }

        [Test]
        public void BindingAPathTwiceThrowsAtTheBinding()
        {
            GrpcProcessorBuilder builder = Builder("/a.B/C");
            var ex = Assert.Throws<InvalidOperationException>(() => Builder("/a.B/C").AddUnary<int, int, IntCodec, IntCodec>("/a.B/C", default, default, x => x));
            Assert.That(ex!.Message, Does.Contain("/a.B/C"));
            Assert.That(builder.Bindings, Has.Count.EqualTo(1));
        }

        [Test]
        public void BindingsDescribeEachRegisteredMethod()
        {
            var contract = new object();
            var builder = new GrpcProcessorBuilder().AddUnary<int, byte[], IntCodec, BytesCodec>("/pkg.Svc/Go", default, default, x => new byte[x], contract);
            BoundMethod bound = builder.Bindings[0];
            Assert.That(bound.Path, Is.EqualTo("/pkg.Svc/Go"));
            Assert.That(bound.ServiceName, Is.EqualTo("pkg.Svc"));
            Assert.That(bound.MethodName, Is.EqualTo("Go"));
            Assert.That(bound.Kind, Is.EqualTo(MethodKind.Unary));
            Assert.That(bound.RequestType, Is.EqualTo(typeof(int)));
            Assert.That(bound.ResponseType, Is.EqualTo(typeof(byte[])));
            Assert.That(bound.Contract, Is.SameAs(contract));
        }

        [Test]
        public void ChecksRunOverEveryBindingAndBuildRefusesOnAnyProblem()
        {
            var seen = new List<string>();
            GrpcProcessorBuilder builder = Builder("/a.B/C", "/a.B/D")
                .AddCheck((bindings, problems) =>
                {
                    foreach (BoundMethod b in bindings) seen.Add(b.Path);
                })
                .AddCheck((bindings, problems) => problems.Add("first"))
                .AddCheck((bindings, problems) => problems.Add("second"));

            Assert.That(builder.Check(), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(seen, Is.EqualTo(new[] { "/a.B/C", "/a.B/D" }));
            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.That(ex!.Message, Does.Contain("first").And.Contain("second"));
        }

        [Test]
        public void PassingChecksBuild()
        {
            GrpcProcessorBuilder builder = Builder("/a.B/C").AddCheck((bindings, problems) => { });
            Assert.That(builder.Check(), Is.Empty);
            Assert.That(builder.Build().Resolve("/a.B/C"u8).IsValid, Is.True);
        }

        [TestCase("")]
        [TestCase("a.B/C")]
        [TestCase("/a.B")]
        [TestCase("/a.B/")]
        [TestCase("//C")]
        [TestCase("/a.B/C/D")]
        public void MalformedPathsAreRejected(string path)
        {
            Assert.Throws<ArgumentException>(() => Builder(path));
        }
    }
}
