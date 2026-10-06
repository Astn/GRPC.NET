using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace GrpcNet.Tests
{
    public class ProcessorTests
    {
        private int _runs;

        private sealed class Harness
        {
            public Harness(GrpcProcessor processor, MethodId method)
            {
                Processor = processor;
                Method = method;
            }

            public GrpcProcessor Processor { get; }

            public MethodId Method { get; }

            public RecordingSink Sink;

            public ArrayBufferWriter<byte> Output { get; } = new ArrayBufferWriter<byte>();

            public GrpcStatus Run(byte[] body, long deadline = 0)
            {
                return Processor.Process(new CallHead(Method, deadline), body, Output, ref Sink);
            }

            public GrpcStatus RunSequence(ReadOnlySequence<byte> body, long deadline = 0)
            {
                return Processor.Process(new CallHead(Method, deadline), in body, Output, ref Sink);
            }
        }

        private Harness Echo(Action<GrpcProcessorOptions>? configure = null, Func<byte[], byte[]>? handler = null)
        {
            var builder = new GrpcProcessorBuilder();
            configure?.Invoke(builder.Options);
            builder.AddUnary<byte[], byte[], BytesCodec, BytesCodec>("/test.Echo/Echo", default, default, request =>
            {
                _runs++;
                return handler != null ? handler(request) : request;
            });
            GrpcProcessor processor = builder.Build();
            return new Harness(processor, processor.Resolve("/test.Echo/Echo"u8));
        }

        private Harness Faulty(Fault fault, Action<GrpcProcessorOptions>? configure = null)
        {
            var builder = new GrpcProcessorBuilder();
            configure?.Invoke(builder.Options);
            builder.AddUnary<byte[], byte[], FaultyCodec, FaultyCodec>("/test.Echo/Echo", new FaultyCodec(fault), new FaultyCodec(fault), request =>
            {
                _runs++;
                return request;
            });
            GrpcProcessor processor = builder.Build();
            return new Harness(processor, processor.Resolve("/test.Echo/Echo"u8));
        }

        [SetUp]
        public void Reset()
        {
            _runs = 0;
        }

        [Test]
        public void UnaryCallWritesOneFramedResponseAndCompletesTheSinkOnce()
        {
            var builder = new GrpcProcessorBuilder();
            builder.AddUnary<int, int, IntCodec, IntCodec>("/calc.Calculator/Double", default, default, x => x * 2);
            GrpcProcessor processor = builder.Build();
            var sink = new RecordingSink();
            var output = new ArrayBufferWriter<byte>();

            GrpcStatus status = processor.Process(new CallHead(processor.Resolve("/calc.Calculator/Double"u8)), Frames.IntFrame(21), output, ref sink);

            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(Frames.IntFrame(42)));
            Assert.That(sink.CompleteCount, Is.EqualTo(1));
            Assert.That(sink.LastStatus.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(sink.HeadersCount, Is.EqualTo(0));
        }

        [Test]
        public void EmptyMessageIsValid()
        {
            Harness h = Echo();
            Assert.That(h.Run(Frames.Frame(ReadOnlySpan<byte>.Empty)).Code, Is.EqualTo(StatusCode.OK));
            Assert.That(h.Output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0, 0 }));
            Assert.That(_runs, Is.EqualTo(1));
        }

        [Test]
        public void SegmentedBodyGivesTheSameResultAtEverySplit()
        {
            byte[] payload = Enumerable.Range(0, 37).Select(i => (byte)i).ToArray();
            byte[] frame = Frames.Frame(payload);
            for (int a = 1; a < frame.Length; a++)
            {
                for (int b = a + 1; b <= frame.Length; b++)
                {
                    Harness h = Echo();
                    ReadOnlySequence<byte> body = Frames.Split(frame, a, b);
                    Assert.That(h.RunSequence(body).Code, Is.EqualTo(StatusCode.OK), $"split {a},{b}");
                    Assert.That(h.Output.WrittenSpan.ToArray(), Is.EqualTo(frame), $"split {a},{b}");
                }
            }
        }

        [Test]
        public void SegmentedBodyLargerThanTheRetainedScratchIsCopiedThroughAPooledBuffer()
        {
            byte[] payload = new byte[GrpcProcessor.RetainedScratchLimit + 1000];
            new Random(1).NextBytes(payload);
            byte[] frame = Frames.Frame(payload);
            Harness h = Echo();
            Assert.That(h.RunSequence(Frames.Split(frame, 3, 4096, 70000)).Code, Is.EqualTo(StatusCode.OK));
            Assert.That(h.Output.WrittenSpan.ToArray(), Is.EqualTo(frame));
        }

        [Test]
        public void ReentrantCallWithASegmentedBodyDoesNotShareScratch()
        {
            byte[] innerFrame = Frames.Frame(new byte[] { 9, 8, 7 });
            byte[] innerResult = Array.Empty<byte>();
            Harness? h = null;
            h = Echo(handler: request =>
            {
                if (request.Length == 1)
                {
                    var innerSink = new RecordingSink();
                    var innerOutput = new ArrayBufferWriter<byte>();
                    ReadOnlySequence<byte> inner = Frames.Split(innerFrame, 2, 6);
                    h!.Processor.Process(new CallHead(h.Method), in inner, innerOutput, ref innerSink);
                    innerResult = innerOutput.WrittenSpan.ToArray();
                }

                return request;
            });

            byte[] outerFrame = Frames.Frame(new byte[] { 1 });
            Assert.That(h.RunSequence(Frames.Split(outerFrame, 2, 5)).Code, Is.EqualTo(StatusCode.OK));
            Assert.That(h.Output.WrittenSpan.ToArray(), Is.EqualTo(outerFrame));
            Assert.That(innerResult, Is.EqualTo(innerFrame));
        }

        [Test]
        public void EachCallStartsFromTheCodecAsBound()
        {
            var builder = new GrpcProcessorBuilder();
            GrpcProcessor? processor = null;
            byte[] nested = Array.Empty<byte>();
            builder.AddUnary<byte[], byte[], CountingCodec, CountingCodec>("/test.Count/Count", default, default, request =>
            {
                if (request.Length == 1)
                {
                    var innerSink = new RecordingSink();
                    var innerOutput = new ArrayBufferWriter<byte>();
                    processor!.Process(new CallHead(processor.Resolve("/test.Count/Count"u8)), Frames.Frame(new byte[2]), innerOutput, ref innerSink);
                    nested = innerOutput.WrittenSpan.ToArray();
                }

                return request;
            });
            processor = builder.Build();
            MethodId id = processor.Resolve("/test.Count/Count"u8);

            for (int i = 0; i < 3; i++)
            {
                var sink = new RecordingSink();
                var output = new ArrayBufferWriter<byte>();
                Assert.That(processor.Process(new CallHead(id), Frames.Frame(new byte[1]), output, ref sink).Code, Is.EqualTo(StatusCode.OK));
                Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(Frames.Frame(new byte[] { 1 })), $"call {i}");
                Assert.That(nested, Is.EqualTo(Frames.Frame(new byte[] { 1 })), $"nested call {i}");
            }
        }

        private static object[] MalformedBodies() => new object[]
        {
            new object[] { "empty", Array.Empty<byte>(), StatusCode.Internal },
            new object[] { "1 prefix byte", new byte[] { 0 }, StatusCode.Internal },
            new object[] { "4 prefix bytes", new byte[] { 0, 0, 0, 0 }, StatusCode.Internal },
            new object[] { "truncated payload", Frames.Frame(new byte[10]).AsSpan(0, 12).ToArray(), StatusCode.Internal },
            new object[] { "trailing byte", Frames.Frame(new byte[3]).Concat(new byte[] { 1 }).ToArray(), StatusCode.Internal },
            new object[] { "second frame", Frames.Frame(new byte[3]).Concat(Frames.Frame(new byte[2])).ToArray(), StatusCode.Internal },
            new object[] { "compressed flag", Frames.Frame(new byte[3], flag: 1), StatusCode.Unimplemented },
            new object[] { "invalid flag", Frames.Frame(new byte[3], flag: 2), StatusCode.Internal },
        };

        [TestCaseSource(nameof(MalformedBodies))]
        public void MalformedBodyIsRejectedBeforeTheMethodRuns(string name, byte[] body, StatusCode expected)
        {
            Harness h = Echo();
            Assert.That(h.Run(body).Code, Is.EqualTo(expected), name);
            Assert.That(h.RunSequence(Frames.Split(body, 1, 3)).Code, Is.EqualTo(expected), name + " (segmented)");
            Assert.That(_runs, Is.EqualTo(0));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));
            Assert.That(h.Sink.CompleteCount, Is.EqualTo(2));
            Assert.That(h.Sink.LastStatus.Code, Is.EqualTo(expected));
        }

        [Test]
        public void OversizeRequestIsRejectedFromItsPrefixBeforeTheMethodRuns()
        {
            Harness h = Echo(o => o.MaxReceiveMessageSize = 16);
            Assert.That(h.Run(Frames.Frame(new byte[17])).Code, Is.EqualTo(StatusCode.ResourceExhausted));

            // Only the prefix is present: the length alone must trigger the rejection.
            byte[] prefixOnly = new byte[5];
            MessageFraming.WritePrefix(prefixOnly, int.MaxValue);
            Assert.That(h.RunSequence(Frames.Split(prefixOnly, 2)).Code, Is.EqualTo(StatusCode.ResourceExhausted));
            Assert.That(_runs, Is.EqualTo(0));
            Assert.That(h.Run(Frames.Frame(new byte[16])).Code, Is.EqualTo(StatusCode.OK));
        }

        [Test]
        public void UnknownMethodIsUnimplemented()
        {
            Harness h = Echo();
            var sink = new RecordingSink();
            GrpcStatus status = h.Processor.Process(new CallHead(MethodId.None), Frames.Frame(new byte[1]), h.Output, ref sink);
            Assert.That(status.Code, Is.EqualTo(StatusCode.Unimplemented));
            Assert.That(sink.CompleteCount, Is.EqualTo(1));
            Assert.That(_runs, Is.EqualTo(0));
        }

        [Test]
        public void DeadlineAlreadyPassedStopsTheCallBeforeTheMethodRuns()
        {
            Harness h = Echo();
            long past = Environment.TickCount64 - 1;
            Assert.That(h.Run(Frames.Frame(new byte[1]), past).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(_runs, Is.EqualTo(0));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));
        }

        [Test]
        public void ExpiredCallIsDeadlineExceededWhateverItsBodyHolds()
        {
            Harness h = Echo();
            long past = Environment.TickCount64 - 1;
            Assert.That(h.Run(Array.Empty<byte>(), past).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(h.Run(new byte[] { 0, 0 }, past).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(h.Run(Frames.Frame(new byte[3], flag: 2), past).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(h.RunSequence(Frames.Split(new byte[] { 0, 0, 0 }, 1), past).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(_runs, Is.EqualTo(0));
        }

        [Test]
        public void DeadlinePassingDuringTheMethodSendsNoSuccess()
        {
            Harness h = Echo(handler: request =>
            {
                Thread.Sleep(60);
                return request;
            });
            long deadline = Environment.TickCount64 + 20;
            Assert.That(h.Run(Frames.Frame(new byte[1]), deadline).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(_runs, Is.EqualTo(1));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));
        }

        [Test]
        public void GrpcExceptionEndsTheCallWithItsCodeAndMessage()
        {
            Harness h = Echo(handler: _ => throw new GrpcException(StatusCode.NotFound, "no such thing"));
            GrpcStatus status = h.Run(Frames.Frame(new byte[1]));
            Assert.That(status.Code, Is.EqualTo(StatusCode.NotFound));
            Assert.That(status.Message.ToArray(), Is.EqualTo("no such thing"u8.ToArray()));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));

            Harness bare = Echo(handler: _ => throw new GrpcException(StatusCode.PermissionDenied));
            GrpcStatus bareStatus = bare.Run(Frames.Frame(new byte[1]));
            Assert.That(bareStatus.Code, Is.EqualTo(StatusCode.PermissionDenied));
            Assert.That(bareStatus.Message.IsEmpty, Is.True);
        }

        [Test]
        public void CancellationMapsToCancelledOrDeadlineExceeded()
        {
            Harness h = Echo(handler: _ => throw new OperationCanceledException());
            Assert.That(h.Run(Frames.Frame(new byte[1])).Code, Is.EqualTo(StatusCode.Cancelled));

            Harness late = Echo(handler: _ =>
            {
                Thread.Sleep(60);
                throw new OperationCanceledException();
            });
            Assert.That(late.Run(Frames.Frame(new byte[1]), Environment.TickCount64 + 20).Code, Is.EqualTo(StatusCode.DeadlineExceeded));
        }

        [Test]
        public void UnexpectedExceptionIsUnknownAndHidesItsMessageUnlessDetailedErrors()
        {
            Harness h = Echo(handler: _ => throw new InvalidOperationException("secret detail"));
            GrpcStatus status = h.Run(Frames.Frame(new byte[1]));
            Assert.That(status.Code, Is.EqualTo(StatusCode.Unknown));
            Assert.That(status.Message.IsEmpty, Is.True);

            Harness detailed = Echo(o => o.DetailedErrors = true, _ => throw new InvalidOperationException("secret detail"));
            Assert.That(detailed.Run(Frames.Frame(new byte[1])).Message.ToArray(), Is.EqualTo("secret detail"u8.ToArray()));
        }

        [Test]
        public void DecodeFailureIsInternalAndTheMethodNeverRuns()
        {
            Harness h = Faulty(Fault.DecodeThrows);
            Assert.That(h.Run(Frames.Frame(new byte[3])).Code, Is.EqualTo(StatusCode.Internal));
            Assert.That(_runs, Is.EqualTo(0));
        }

        [TestCase(Fault.SizeThrows, StatusCode.Internal)]
        [TestCase(Fault.SizeNegative, StatusCode.Internal)]
        [TestCase(Fault.EncodeThrows, StatusCode.Internal)]
        public void ResponseCodecFailureCommitsNothing(Fault fault, StatusCode expected)
        {
            Harness h = Faulty(fault);
            Assert.That(h.Run(Frames.Frame(new byte[100])).Code, Is.EqualTo(expected));
            Assert.That(_runs, Is.EqualTo(1));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));
        }

        [Test]
        public void EncodeFailureOnTheStagedPathCommitsNothing()
        {
            Harness h = Faulty(Fault.EncodeThrows, o => o.MaxContiguousResponseBytes = 16);
            Assert.That(h.Run(Frames.Frame(new byte[100])).Code, Is.EqualTo(StatusCode.Internal));
            Assert.That(_runs, Is.EqualTo(1));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));
        }

        [Test]
        public void ResponseAboveTheSendLimitIsResourceExhausted()
        {
            Harness h = Echo(o => o.MaxSendMessageSize = 8, request => new byte[9]);
            Assert.That(h.Run(Frames.Frame(new byte[1])).Code, Is.EqualTo(StatusCode.ResourceExhausted));
            Assert.That(_runs, Is.EqualTo(1));
            Assert.That(h.Output.WrittenCount, Is.EqualTo(0));
        }

        [Test]
        public void LargeUnaryResponseIsWrittenContiguouslyWithoutACap()
        {
            byte[] large = new byte[314159];
            new Random(2).NextBytes(large);
            var writer = new SmallSpanWriter(64);
            var builder = new GrpcProcessorBuilder();
            builder.AddUnary<byte[], byte[], BytesCodec, BytesCodec>("/grpc.testing.TestService/UnaryCall", default, default, _ => large);
            GrpcProcessor processor = builder.Build();
            var sink = new RecordingSink();

            GrpcStatus status = processor.Process(new CallHead(processor.Resolve("/grpc.testing.TestService/UnaryCall"u8)), Frames.Frame(new byte[1]), writer, ref sink);

            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(writer.Written, Is.EqualTo(Frames.Frame(large)));
            Assert.That(writer.GetSpanCalls, Is.EqualTo(1));
        }

        [Test]
        public void LargeUnaryResponseAboveTheHostCapIsStagedAndCopiedInChunks()
        {
            byte[] large = new byte[314159];
            new Random(3).NextBytes(large);
            var writer = new SmallSpanWriter(4096);
            var builder = new GrpcProcessorBuilder();
            builder.Options.MaxContiguousResponseBytes = 1024;
            builder.AddUnary<byte[], byte[], BytesCodec, BytesCodec>("/grpc.testing.TestService/UnaryCall", default, default, _ => large);
            GrpcProcessor processor = builder.Build();
            var sink = new RecordingSink();

            GrpcStatus status = processor.Process(new CallHead(processor.Resolve("/grpc.testing.TestService/UnaryCall"u8)), Frames.Frame(new byte[1]), writer, ref sink);

            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(writer.Written, Is.EqualTo(Frames.Frame(large)));
            Assert.That(writer.GetSpanCalls, Is.GreaterThan(300));
        }

        [Test]
        public void InvalidOptionsAreRejectedAtBuild()
        {
            var builder = new GrpcProcessorBuilder();
            builder.Options.MaxSendMessageSize = int.MaxValue;
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build());

            var capped = new GrpcProcessorBuilder();
            capped.Options.MaxContiguousResponseBytes = 4;
            Assert.Throws<ArgumentOutOfRangeException>(() => capped.Build());
        }

        [Test]
        public void OptionsAreCopiedAtBuild()
        {
            var builder = new GrpcProcessorBuilder();
            builder.AddUnary<byte[], byte[], BytesCodec, BytesCodec>("/test.Echo/Echo", default, default, r => r);
            GrpcProcessor processor = builder.Build();
            builder.Options.MaxReceiveMessageSize = 1;
            var sink = new RecordingSink();
            var output = new ArrayBufferWriter<byte>();
            Assert.That(processor.Process(new CallHead(processor.Resolve("/test.Echo/Echo"u8)), Frames.Frame(new byte[10]), output, ref sink).Code, Is.EqualTo(StatusCode.OK));
        }
    }
}
