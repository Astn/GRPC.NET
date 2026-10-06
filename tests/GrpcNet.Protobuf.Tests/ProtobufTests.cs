using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using GrpcNet.Protobuf.Tests.Messages;
using GrpcNet.Protobuf.Tests.Swapped;
using NUnit.Framework;

namespace GrpcNet.Protobuf.Tests
{
    /// <summary>The method objects the generator will emit for test.proto, written by hand until it exists.</summary>
    internal static class Calculator
    {
        public static readonly UnaryMethod<Scalar, Scalar> Add = new(TestReflection.Descriptor, "test.Calculator", "Add");
        public static readonly UnaryMethod<Blob, Blob> Echo = new(TestReflection.Descriptor, "test.Calculator", "Echo");
        public static readonly UnaryMethod<Nested, Text> Describe = new(TestReflection.Descriptor, "test.Calculator", "Describe");
        public static readonly UnaryMethod<Messages.Empty, Messages.Empty> Nothing = new(TestReflection.Descriptor, "test.Calculator", "Nothing");
        public static readonly ServerStreamingMethod<Scalar, Scalar> Watch = new(TestReflection.Descriptor, "test.Calculator", "Watch");
    }

    public class ProtobufTests
    {
        private static byte[] Frame(byte[] payload)
        {
            var frame = new byte[5 + payload.Length];
            MessageFraming.WritePrefix(frame, payload.Length);
            payload.CopyTo(frame, 5);
            return frame;
        }

        private static (GrpcStatus Status, byte[] Output) Call(GrpcProcessor processor, GrpcMethod method, byte[] body)
        {
            var sink = new NullSink();
            var output = new ArrayBufferWriter<byte>();
            GrpcStatus status = processor.Process(new CallHead(processor.Resolve(method.PathUtf8.Span)), body, output, ref sink);
            return (status, output.WrittenSpan.ToArray());
        }

        private struct NullSink : ICallSink
        {
            public void WriteHeaders(in MetadataView headers)
            {
            }

            public void Complete(in GrpcStatus status, in MetadataView trailers)
            {
            }
        }

        [Test]
        public void TypedUnaryCallRoundTripsAProtobufMessage()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .Bind(Calculator.Add, r => new Scalar { A = r.A + (int)r.B })
                .Build();

            var request = new Scalar { A = 40, B = 2 };
            (GrpcStatus status, byte[] output) = Call(processor, Calculator.Add, Frame(request.ToByteArray()));

            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(output, Is.EqualTo(Frame(new Scalar { A = 42 }.ToByteArray())));
        }

        [Test]
        public void SegmentedBodyDecodesTheSameMessage()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(Calculator.Echo, r => r).Build();
            var request = new Blob { Data = ByteString.CopyFrom(Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray()) };
            byte[] frame = Frame(request.ToByteArray());

            var first = new Segment(frame.AsMemory(0, 3));
            Segment last = first.Append(frame.AsMemory(3, 1000)).Append(frame.AsMemory(1003));
            var body = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            var sink = new NullSink();
            var output = new ArrayBufferWriter<byte>();

            GrpcStatus status = processor.Process(new CallHead(processor.Resolve(Calculator.Echo.PathUtf8.Span)), in body, output, ref sink);

            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(frame));
        }

        [Test]
        public void EmptyMessageRoundTrips()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(Calculator.Nothing, r => r).Build();
            (GrpcStatus status, byte[] output) = Call(processor, Calculator.Nothing, Frame(Array.Empty<byte>()));
            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(output, Is.EqualTo(new byte[5]));
        }

        [Test]
        public void MalformedProtobufIsInternalAndTheMethodNeverRuns()
        {
            int runs = 0;
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .Bind(Calculator.Add, r =>
                {
                    runs++;
                    return r;
                })
                .Build();

            // Field 1 as a varint whose continuation bit promises a byte that never comes.
            (GrpcStatus status, byte[] output) = Call(processor, Calculator.Add, Frame(new byte[] { 0x08, 0x80 }));

            Assert.That(status.Code, Is.EqualTo(StatusCode.Internal));
            Assert.That(runs, Is.EqualTo(0));
            Assert.That(output, Is.Empty);
        }

        private static IEnumerable<TestCaseData> Shapes()
        {
            yield return new TestCaseData(new Scalar { A = -1, B = long.MaxValue, C = Math.PI, D = true, E = 7 }).SetName("scalar");
            yield return new TestCaseData(new Text { Value = "héllo, wörld — ✓" }).SetName("string");
            var nested = new Nested { Inner = new Scalar { A = 1 } };
            nested.Items.Add(new Scalar { B = 2 });
            nested.Items.Add(new Scalar { C = 3.5 });
            nested.Tags.Add("x", 1);
            nested.Tags.Add("y", -1);
            yield return new TestCaseData(nested).SetName("nested, repeated and map");
            yield return new TestCaseData(new Blob { Data = ByteString.CopyFrom(new byte[70000]) }).SetName("bytes");
            yield return new TestCaseData(new Messages.Empty()).SetName("empty");
        }

        [TestCaseSource(nameof(Shapes))]
        public void CodecMatchesGoogleProtobuf(IMessage message)
        {
            byte[] expected = message.ToByteArray();
            byte[] actual = message switch
            {
                Scalar m => Encode(new ProtobufCodec<Scalar>(Scalar.Parser), m),
                Text m => Encode(new ProtobufCodec<Text>(Text.Parser), m),
                Nested m => Encode(new ProtobufCodec<Nested>(Nested.Parser), m),
                Blob m => Encode(new ProtobufCodec<Blob>(Blob.Parser), m),
                Messages.Empty m => Encode(new ProtobufCodec<Messages.Empty>(Messages.Empty.Parser), m),
                _ => throw new InvalidOperationException(),
            };

            Assert.That(actual, Is.EqualTo(expected));
        }

        private static byte[] Encode<T>(ProtobufCodec<T> codec, T message)
            where T : class, IMessage<T>
        {
            int size = codec.Size(in message);
            var buffer = new byte[size];
            codec.Encode(in message, size, buffer);
            Assert.That(codec.Decode(buffer), Is.EqualTo(message));
            return buffer;
        }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(ReadOnlyMemory<byte> memory)
            {
                Memory = memory;
            }

            public Segment Append(ReadOnlyMemory<byte> memory)
            {
                var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
                Next = next;
                return next;
            }
        }
    }

    public class MethodObjectTests
    {
        [Test]
        public void MethodObjectCarriesItsPathAndDescriptor()
        {
            Assert.That(Calculator.Add.Path, Is.EqualTo("/test.Calculator/Add"));
            Assert.That(Calculator.Add.PathUtf8.ToArray(), Is.EqualTo("/test.Calculator/Add"u8.ToArray()));
            Assert.That(Calculator.Add.Descriptor.FullName, Is.EqualTo("test.Calculator.Add"));
            Assert.That(Calculator.Add.Kind, Is.EqualTo(MethodKind.Unary));
            Assert.That(Calculator.Watch.Kind, Is.EqualTo(MethodKind.ServerStreaming));
        }

        [Test]
        public void WrongRequestTypeIsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new UnaryMethod<Blob, Scalar>(TestReflection.Descriptor, "test.Pair", "Left"));
            Assert.That(ex!.Message, Does.Contain("test.Pair.Left").And.Contain("takes"));
        }

        [Test]
        public void WrongResponseTypeIsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new UnaryMethod<Scalar, Text>(TestReflection.Descriptor, "test.Pair", "Left"));
            Assert.That(ex!.Message, Does.Contain("returns"));
        }

        [Test]
        public void WrongKindIsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new UnaryMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Calculator", "Watch"));
            Assert.That(ex!.Message, Does.Contain("ServerStreaming"));
            Assert.Throws<InvalidOperationException>(() => new DuplexMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Calculator", "Add"));
            Assert.Throws<InvalidOperationException>(() => new ClientStreamingMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Calculator", "Add"));
        }

        [Test]
        public void UnknownServiceOrMethodIsRejected()
        {
            Assert.Throws<InvalidOperationException>(() => new UnaryMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Nope", "Left"));
            Assert.Throws<InvalidOperationException>(() => new UnaryMethod<Scalar, Scalar>(TestReflection.Descriptor, "Pair", "Left"));
            Assert.Throws<InvalidOperationException>(() => new UnaryMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Pair", "Middle"));
        }

        [Test]
        public void MethodsAreResolvedByNameNotPosition()
        {
            var left = new UnaryMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Pair", "Left");
            var swappedLeft = new UnaryMethod<Scalar, Scalar>(SwappedReflection.Descriptor, "test.swapped.Pair", "Left");
            var swappedRight = new UnaryMethod<Scalar, Scalar>(SwappedReflection.Descriptor, "test.swapped.Pair", "Right");

            Assert.That(left.Descriptor.Index, Is.EqualTo(0));
            Assert.That(swappedLeft.Descriptor.Index, Is.EqualTo(1));
            Assert.That(swappedLeft.Descriptor.Name, Is.EqualTo("Left"));
            Assert.That(swappedLeft.Path, Is.EqualTo("/test.swapped.Pair/Left"));

            GrpcProcessor processor = new GrpcProcessorBuilder()
                .AddService(SwappedReflection.Descriptor.Services[0], requireComplete: true)
                .Bind(swappedLeft, _ => new Scalar { A = 1 })
                .Bind(swappedRight, _ => new Scalar { A = 2 })
                .Build();

            Assert.That(Invoke(processor, swappedLeft).A, Is.EqualTo(1));
            Assert.That(Invoke(processor, swappedRight).A, Is.EqualTo(2));
        }

        private static Scalar Invoke(GrpcProcessor processor, GrpcMethod method)
        {
            var frame = new byte[5];
            var sink = new NullSink();
            var output = new ArrayBufferWriter<byte>();
            GrpcStatus status = processor.Process(new CallHead(processor.Resolve(method.PathUtf8.Span)), frame, output, ref sink);
            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            return Scalar.Parser.ParseFrom(output.WrittenSpan.Slice(5));
        }

        private struct NullSink : ICallSink
        {
            public void WriteHeaders(in MetadataView headers)
            {
            }

            public void Complete(in GrpcStatus status, in MetadataView trailers)
            {
            }
        }
    }

    public class ContractCheckTests
    {
        private static readonly Google.Protobuf.Reflection.ServiceDescriptor CalculatorService = TestReflection.Descriptor.Services.Single(s => s.Name == "Calculator");
        private static readonly Google.Protobuf.Reflection.ServiceDescriptor PairService = TestReflection.Descriptor.Services.Single(s => s.Name == "Pair");

        private static ProtobufCodec<T> Codec<T>(MessageParser<T> parser)
            where T : class, IMessage<T> => new ProtobufCodec<T>(parser);

        [Test]
        public void TypedBindingsOfAPartialServicePass()
        {
            var builder = new GrpcProcessorBuilder().Bind(Calculator.Add, r => r);
            Assert.That(builder.Check(), Is.Empty);
            Assert.That(builder.Build(), Is.Not.Null);
        }

        [Test]
        public void UnknownMethodUnderARegisteredServiceIsReported()
        {
            var builder = new GrpcProcessorBuilder()
                .AddService(CalculatorService, requireComplete: false)
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>>("/test.Calculator/Subtract", Codec(Scalar.Parser), Codec(Scalar.Parser), r => r);

            Assert.That(builder.Check(), Has.Exactly(1).Contains("declares no method 'Subtract'"));
        }

        [Test]
        public void RawBindingWithWrongTypesIsReported()
        {
            var builder = new GrpcProcessorBuilder()
                .AddService(CalculatorService, requireComplete: false)
                .AddUnary<Blob, Text, ProtobufCodec<Blob>, ProtobufCodec<Text>>("/test.Calculator/Add", Codec(Blob.Parser), Codec(Text.Parser), r => new Text());

            IReadOnlyList<string> problems = builder.Check();
            Assert.That(problems, Has.Count.EqualTo(1));
            Assert.That(problems[0], Does.Contain("takes").And.Contain("returns"));
        }

        [Test]
        public void RawBindingOfTheWrongKindIsReported()
        {
            var builder = new GrpcProcessorBuilder()
                .AddService(CalculatorService, requireComplete: false)
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>>("/test.Calculator/Watch", Codec(Scalar.Parser), Codec(Scalar.Parser), r => r);

            Assert.That(builder.Check(), Has.Exactly(1).Contains("is bound as Unary, but the .proto declares ServerStreaming"));
        }

        [Test]
        public void BindingWithAnotherMethodsContractIsReported()
        {
            Google.Protobuf.Reflection.MethodDescriptor right = PairService.FindMethodByName("Right");
            var builder = new GrpcProcessorBuilder()
                .AddService(PairService, requireComplete: false)
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>>("/test.Pair/Left", Codec(Scalar.Parser), Codec(Scalar.Parser), r => r, right);

            Assert.That(builder.Check(), Has.Exactly(1).Contains("method object for 'test.Pair.Right'"));
        }

        [Test]
        public void BindingAMethodTwiceThrowsAtTheBinding()
        {
            var builder = new GrpcProcessorBuilder().Bind(Calculator.Add, r => r);
            var ex = Assert.Throws<InvalidOperationException>(() => builder.Bind(Calculator.Add, r => r));
            Assert.That(ex!.Message, Does.Contain("/test.Calculator/Add"));
        }

        [Test]
        public void IncompleteServiceListsEveryUnboundMethod()
        {
            // Bound first, which registers the service as partial; the later registration upgrades it to complete.
            var builder = new GrpcProcessorBuilder()
                .Bind(Calculator.Add, r => r)
                .Bind(Calculator.Describe, r => new Text())
                .AddService(CalculatorService, requireComplete: true);

            IReadOnlyList<string> problems = builder.Check();
            Assert.That(problems, Has.Count.EqualTo(1));
            Assert.That(problems[0], Does.Contain("unbound: Echo, Nothing, Watch."));
            Assert.That(builder.Check(), Is.EqualTo(problems), "Check is repeatable");

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.That(ex!.Message, Does.Contain("unbound: Echo, Nothing, Watch."));
        }

        [Test]
        public void RegisteringACompleteServiceAgainAsPartialKeepsItComplete()
        {
            var builder = new GrpcProcessorBuilder()
                .AddService(PairService, requireComplete: true)
                .Bind(new UnaryMethod<Scalar, Scalar>(TestReflection.Descriptor, "test.Pair", "Left"), r => r);

            Assert.That(builder.Check(), Has.Exactly(1).Contains("unbound: Right."));
        }

        [Test]
        public void BuildListsEveryProblemAtOnce()
        {
            var builder = new GrpcProcessorBuilder()
                .AddService(CalculatorService, requireComplete: true)
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>>("/test.Calculator/Subtract", Codec(Scalar.Parser), Codec(Scalar.Parser), r => r)
                .AddUnary<Blob, Blob, ProtobufCodec<Blob>, ProtobufCodec<Blob>>("/test.Calculator/Add", Codec(Blob.Parser), Codec(Blob.Parser), r => r);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.That(ex!.Message, Does.Contain("Subtract").And.Contain("takes").And.Contain("unbound: Echo, Describe, Nothing, Watch."));
        }

        [Test]
        public void MethodsOfUnregisteredServicesAreNotChecked()
        {
            var builder = new GrpcProcessorBuilder()
                .Bind(Calculator.Add, r => r)
                .AddUnary<Blob, Blob, ProtobufCodec<Blob>, ProtobufCodec<Blob>>("/other.Service/Anything", Codec(Blob.Parser), Codec(Blob.Parser), r => r);

            Assert.That(builder.Check(), Is.Empty);
        }
    }
}
