using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GrpcNet.Tests
{
    /// <summary>How the test host answers after each response message.</summary>
    public enum HostPolicy
    {
        Buffer,
        FlushEach,
        Credit,
        ThrowSync,
        FaultAsync,
    }

    /// <summary>An in-memory host's per-stream state.</summary>
    internal sealed class TestCallState : HostCallState
    {
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private TaskCompletionSource? _credit;
        private int _queued;

        public TestCallState(HostPolicy policy = HostPolicy.Buffer, int creditLimit = int.MaxValue)
        {
            Policy = policy;
            CreditLimit = creditLimit;
        }

        public HostPolicy Policy { get; set; }

        public int CreditLimit { get; }

        public int MessagesWritten { get; private set; }

        public List<int> HookThreads { get; } = new List<int>();

        public bool WaitingForCredit => _credit != null;

        public override CancellationToken Cancellation => _cancellation.Token;

        public void Cancel() => _cancellation.Cancel();

        public void ReleaseCredit()
        {
            _queued = 0;
            TaskCompletionSource? waiting = _credit;
            _credit = null;
            waiting?.SetResult();
        }

        protected override ValueTask OnMessageWritten(PipeWriter output, int framedBytes)
        {
            MessagesWritten++;
            HookThreads.Add(Environment.CurrentManagedThreadId);
            switch (Policy)
            {
                case HostPolicy.FlushEach:
                    return FlushAsync(output);
                case HostPolicy.Credit:
                    _queued += framedBytes;
                    if (_queued <= CreditLimit)
                    {
                        return default;
                    }

                    // One wait per stream, so a release always reaches every write that is waiting.
                    _credit ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    return new ValueTask(_credit.Task);
                case HostPolicy.ThrowSync:
                    throw new IOException("The transport failed.");
                case HostPolicy.FaultAsync:
                    return ValueTask.FromException(new IOException("The transport failed."));
                default:
                    return default;
            }
        }

        private static async ValueTask FlushAsync(PipeWriter output) => await output.FlushAsync();
    }

    internal sealed class SinkRecord
    {
        public int CompleteCount;
        public GrpcStatus LastStatus;
    }

    /// <summary>A sink that records into a shared object, since ProcessAsync takes the sink by value.</summary>
    internal readonly struct SharedSink : ICallSink
    {
        private readonly SinkRecord _record;

        public SharedSink(SinkRecord record)
        {
            _record = record;
        }

        public void WriteHeaders(in MetadataView headers)
        {
        }

        public void Complete(in GrpcStatus status, in MetadataView trailers)
        {
            _record.CompleteCount++;
            _record.LastStatus = status;
        }
    }

    /// <summary>A PipeWriter that cannot report unflushed bytes and counts flushes.</summary>
    internal sealed class CountingWriter : PipeWriter
    {
        private readonly PipeWriter _inner;

        public CountingWriter(PipeWriter inner)
        {
            _inner = inner;
        }

        public int Flushes { get; private set; }

        public override bool CanGetUnflushedBytes => false;

        public override long UnflushedBytes => throw new NotSupportedException("The core must not ask.");

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Flushes++;
            return _inner.FlushAsync(cancellationToken);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
    }

    /// <summary>A PipeWriter that can block in GetSpan, or fail in GetSpan or Advance.</summary>
    internal sealed class TrappedWriter : PipeWriter
    {
        private readonly PipeWriter _inner;

        public TrappedWriter(PipeWriter inner)
        {
            _inner = inner;
        }

        public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();

        public ManualResetEventSlim Release { get; } = new ManualResetEventSlim();

        public bool BlockNextGetSpan { get; set; }

        public bool FailGetSpan { get; set; }

        public bool FailAdvance { get; set; }

        public override void Advance(int bytes)
        {
            if (FailAdvance)
            {
                // Part of the frame is committed before the pipe fails.
                _inner.Advance(Math.Min(bytes, 3));
                throw new IOException("The pipe failed in Advance.");
            }

            _inner.Advance(bytes);
        }

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            Trap();
            return _inner.GetMemory(sizeHint);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            Trap();
            return _inner.GetSpan(sizeHint);
        }

        private void Trap()
        {
            if (FailGetSpan)
            {
                throw new IOException("The pipe failed in GetSpan.");
            }

            if (BlockNextGetSpan)
            {
                BlockNextGetSpan = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }

    internal sealed class ThrowingReader : PipeReader
    {
        private readonly bool _inTryRead;

        public ThrowingReader(bool inTryRead) => _inTryRead = inTryRead;

        public override bool TryRead(out ReadResult result)
        {
            if (_inTryRead)
            {
                throw new IOException("TryRead failed.");
            }

            result = default;
            return false;
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => throw new IOException("ReadAsync failed.");

        public override void AdvanceTo(SequencePosition consumed)
        {
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
        }

        public override void CancelPendingRead()
        {
        }

        public override void Complete(Exception? exception = null)
        {
        }
    }

    public class StreamingTests
    {
        private const string Path = "/test.Stream/Call";

        private static byte[] Concat(params byte[][] frames) => frames.SelectMany(f => f).ToArray();

        private static async Task<List<byte[]>> ReadFramesAsync(PipeReader reader)
        {
            var frames = new List<byte[]>();
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                ReadOnlySequence<byte> buffer = result.Buffer;
                while (MessageFraming.TryReadPrefix(in buffer, out _, out uint length) && buffer.Length >= 5 + length)
                {
                    frames.Add(buffer.Slice(5, length).ToArray());
                    buffer = buffer.Slice(5 + length);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    Assert.That(buffer.IsEmpty, Is.True, "a partial response frame");
                    return frames;
                }
            }
        }

        private sealed class Outcome
        {
            public GrpcStatus Status;
            public List<byte[]> Responses = new List<byte[]>();
        }

        /// <summary>Runs one call with the whole request already written, as a host whose body has arrived.</summary>
        private static async Task<Outcome> RunAsync(GrpcProcessor processor, byte[] request, HostCallState? state = null, long deadline = 0, string path = Path)
        {
            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            await requestPipe.Writer.WriteAsync(request);
            requestPipe.Writer.Complete();

            var record = new SinkRecord();
            await processor.ProcessAsync(new CallHead(processor.Resolve(System.Text.Encoding.UTF8.GetBytes(path)), deadline, state), requestPipe.Reader, responsePipe.Writer, new SharedSink(record))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            // The host's part: flush what the core left buffered and end the response.
            await responsePipe.Writer.FlushAsync();
            responsePipe.Writer.Complete();
            Assert.That(record.CompleteCount, Is.EqualTo(1), "Complete exactly once");
            return new Outcome { Status = record.LastStatus, Responses = await ReadFramesAsync(responsePipe.Reader) };
        }

        private static int Int(byte[] payload) => BinaryPrimitives.ReadInt32LittleEndian(payload);

        private static GrpcProcessor Build(Action<GrpcProcessorBuilder> bind, Action<GrpcProcessorOptions>? configure = null)
        {
            var builder = new GrpcProcessorBuilder();
            configure?.Invoke(builder.Options);
            bind(builder);
            return builder.Build();
        }

        [Test]
        public async Task AsyncUnaryCallRoundTrips()
        {
            GrpcProcessor processor = Build(b => b.AddAsyncUnary<int, int, IntCodec, IntCodec>(Path, default, default, async (x, ct) =>
            {
                await Task.Yield();
                return x * 2;
            }));

            Outcome outcome = await RunAsync(processor, Frames.IntFrame(21));
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { 42 }));
            Assert.That(processor.IsSynchronousUnary(processor.Resolve("/test.Stream/Call"u8)), Is.False);
        }

        [Test]
        public async Task SynchronousUnaryMethodAlsoRunsThroughProcessAsync()
        {
            GrpcProcessor processor = Build(b => b.AddUnary<int, int, IntCodec, IntCodec>(Path, default, default, x => x + 1));
            Assert.That(processor.IsSynchronousUnary(processor.Resolve("/test.Stream/Call"u8)), Is.True);
            Outcome outcome = await RunAsync(processor, Frames.IntFrame(1));
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void SynchronousProcessRefusesAnAsynchronousMethod()
        {
            GrpcProcessor processor = Build(b => b.AddAsyncUnary<int, int, IntCodec, IntCodec>(Path, default, default, (x, ct) => new ValueTask<int>(x)));
            var sink = new RecordingSink();
            var output = new ArrayBufferWriter<byte>();
            Assert.That(processor.Process(new CallHead(processor.Resolve("/test.Stream/Call"u8)), Frames.IntFrame(1), output, ref sink).Code, Is.EqualTo(StatusCode.Internal));
            Assert.That(output.WrittenCount, Is.EqualTo(0));
        }

        [Test]
        public async Task UnknownMethodIsUnimplemented()
        {
            GrpcProcessor processor = Build(b => b.AddUnary<int, int, IntCodec, IntCodec>(Path, default, default, x => x));
            Outcome outcome = await RunAsync(processor, Frames.IntFrame(1), path: "/test.Stream/Nope");
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.Unimplemented));
        }

        private static object[] WrongRequestCounts() => new object[]
        {
            new object[] { "no message", Array.Empty<byte>() },
            new object[] { "two messages", Concat(Frames.IntFrame(1), Frames.IntFrame(2)) },
        };

        [TestCaseSource(nameof(WrongRequestCounts))]
        public async Task UnaryAndServerStreamingNeedExactlyOneRequestBeforeTheHandlerRuns(string name, byte[] request)
        {
            int runs = 0;
            GrpcProcessor unary = Build(b => b.AddAsyncUnary<int, int, IntCodec, IntCodec>(Path, default, default, (x, ct) =>
            {
                runs++;
                return new ValueTask<int>(x);
            }));
            GrpcProcessor streaming = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, (x, w, ct) =>
            {
                runs++;
                return default;
            }));

            Assert.That((await RunAsync(unary, request)).Status.Code, Is.EqualTo(StatusCode.Internal), name);
            Assert.That((await RunAsync(streaming, request)).Status.Code, Is.EqualTo(StatusCode.Internal), name);
            Assert.That(runs, Is.EqualTo(0));
        }

        [Test]
        public async Task ServerStreamingWritesEveryMessage()
        {
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (count, writer, ct) =>
            {
                for (int i = 0; i < count; i++)
                {
                    await writer.WriteAsync(i);
                }
            }));

            var state = new TestCallState();
            Outcome outcome = await RunAsync(processor, Frames.IntFrame(5), state);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            Assert.That(state.MessagesWritten, Is.EqualTo(5));

            Outcome none = await RunAsync(processor, Frames.IntFrame(0));
            Assert.That(none.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(none.Responses, Is.Empty);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(50)]
        public async Task ClientStreamingReadsToTheEnd(int count)
        {
            GrpcProcessor processor = Build(b => b.AddClientStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, ct) =>
            {
                int sum = 0;
                while (await reader.MoveNextAsync())
                {
                    sum += reader.Current;
                }

                return sum;
            }));

            byte[] request = Concat(Enumerable.Range(1, count).Select(Frames.IntFrame).ToArray());
            Outcome outcome = await RunAsync(processor, request);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { count * (count + 1) / 2 }));
        }

        [Test]
        public async Task CurrentStaysValidAfterTheNextRead()
        {
            byte[]? first = null;
            GrpcProcessor processor = Build(b => b.AddClientStreaming<byte[], byte[], BytesCodec, BytesCodec>(Path, default, default, async (reader, ct) =>
            {
                Assert.That(await reader.MoveNextAsync(), Is.True);
                first = reader.Current;
                Assert.That(await reader.MoveNextAsync(), Is.True);
                Assert.That(await reader.MoveNextAsync(), Is.False);
                return first;
            }));

            Outcome outcome = await RunAsync(processor, Concat(Frames.Frame(new byte[] { 1, 2, 3 }), Frames.Frame(new byte[] { 9, 9, 9, 9 })));
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(first, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(outcome.Responses.Single(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public async Task DuplexInterleavesRequestsAndResponses()
        {
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
            {
                while (await reader.MoveNextAsync())
                {
                    await writer.WriteAsync(reader.Current * 10);
                }
            }));

            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            var record = new SinkRecord();
            var state = new TestCallState(HostPolicy.FlushEach);
            Task call = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8), 0, state), requestPipe.Reader, responsePipe.Writer, new SharedSink(record)).AsTask();

            for (int i = 1; i <= 3; i++)
            {
                // Each response arrives before the next request is sent.
                await requestPipe.Writer.WriteAsync(Frames.IntFrame(i));
                ReadResult result = await responsePipe.Reader.ReadAsync();
                Assert.That(result.Buffer.Length, Is.EqualTo(9));
                Assert.That(Int(result.Buffer.Slice(5).ToArray()), Is.EqualTo(i * 10));
                responsePipe.Reader.AdvanceTo(result.Buffer.End);
            }

            requestPipe.Writer.Complete();
            await call;
            Assert.That(record.CompleteCount, Is.EqualTo(1));
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
        }

        [Test]
        public async Task FramesSplitAcrossWritesAtEveryOffsetAreReassembled()
        {
            GrpcProcessor processor = Build(b => b.AddClientStreaming<byte[], int, BytesCodec, IntCodec>(Path, default, default, async (reader, ct) =>
            {
                int total = 0;
                while (await reader.MoveNextAsync())
                {
                    total += reader.Current.Sum(x => x);
                }

                return total;
            }));

            byte[] request = Concat(Frames.Frame(new byte[] { 1, 2, 3 }), Frames.Frame(Array.Empty<byte>()), Frames.Frame(new byte[] { 4, 5 }));
            for (int cut = 1; cut < request.Length; cut++)
            {
                Assert.That(await RunSplitAsync(processor, request, cut), Is.EqualTo(15), $"cut at {cut}");
            }

            Assert.That(await RunSplitAsync(processor, request, 1, everyByte: true), Is.EqualTo(15), "one byte per write");
        }

        private static async Task<int> RunSplitAsync(GrpcProcessor processor, byte[] request, int cut, bool everyByte = false)
        {
            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            var record = new SinkRecord();
            Task call = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, responsePipe.Writer, new SharedSink(record)).AsTask();

            if (everyByte)
            {
                foreach (byte b in request)
                {
                    await requestPipe.Writer.WriteAsync(new[] { b });
                    await Task.Yield();
                }
            }
            else
            {
                await requestPipe.Writer.WriteAsync(request.AsMemory(0, cut));
                await Task.Yield();
                await requestPipe.Writer.WriteAsync(request.AsMemory(cut));
            }

            requestPipe.Writer.Complete();
            await call;
            await responsePipe.Writer.FlushAsync();
            responsePipe.Writer.Complete();
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
            return Int((await ReadFramesAsync(responsePipe.Reader)).Single());
        }

        private static object[] ProtocolFaults() => new object[]
        {
            new object[] { "invalid flag", Frames.Frame(new byte[] { 1, 2, 3, 4 }, flag: 2), StatusCode.Internal },
            new object[] { "compressed", Frames.Frame(new byte[] { 1, 2, 3, 4 }, flag: 1), StatusCode.Unimplemented },
            new object[] { "oversize", Frames.Frame(new byte[9]), StatusCode.ResourceExhausted },
            new object[] { "ends inside a prefix", new byte[] { 0, 0 }, StatusCode.Internal },
            new object[] { "ends inside a message", Frames.Frame(new byte[4]).AsSpan(0, 7).ToArray(), StatusCode.Internal },
            new object[] { "undecodable", Frames.Frame(new byte[3]), StatusCode.Internal },
        };

        [TestCaseSource(nameof(ProtocolFaults))]
        public async Task ProtocolFaultsEndTheCallEvenWhenTheHandlerSwallowsThem(string name, byte[] bad, StatusCode expected)
        {
            bool sawFault = false;
            bool writeRefused = false;
            GrpcProcessor processor = Build(
                b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
                {
                    try
                    {
                        while (await reader.MoveNextAsync())
                        {
                            await writer.WriteAsync(reader.Current);
                        }
                    }
                    catch (GrpcException)
                    {
                        sawFault = true;
                    }

                    try
                    {
                        await writer.WriteAsync(99);
                    }
                    catch (GrpcException)
                    {
                        writeRefused = true;
                    }

                    // Returns normally: the handler claims success.
                }),
                o => o.MaxReceiveMessageSize = 8);

            Outcome outcome = await RunAsync(processor, Concat(Frames.IntFrame(7), bad));
            Assert.That(outcome.Status.Code, Is.EqualTo(expected), name);
            Assert.That(sawFault && writeRefused, Is.True, name);
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { 7 }), name + ": nothing after the fault");
        }

        [Test]
        public async Task ExpiredCallNeverRunsTheHandler()
        {
            int runs = 0;
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, (r, w, ct) =>
            {
                runs++;
                return default;
            }));

            Outcome outcome = await RunAsync(processor, Frames.IntFrame(1), deadline: Environment.TickCount64 - 1);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(runs, Is.EqualTo(0));
        }

        [Test]
        public async Task DeadlinePassingBetweenMessagesStopsTheStream()
        {
            bool refused = false;
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (x, writer, ct) =>
            {
                await writer.WriteAsync(1);
                await Task.Delay(80);
                try
                {
                    await writer.WriteAsync(2);
                }
                catch (GrpcException ex) when (ex.Code == StatusCode.DeadlineExceeded)
                {
                    refused = true;
                }
            }));

            Outcome outcome = await RunAsync(processor, Frames.IntFrame(0), deadline: Environment.TickCount64 + 30);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.DeadlineExceeded));
            Assert.That(refused, Is.True);
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public async Task CancellationEndsTheCallCancelled()
        {
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
            {
                while (await reader.MoveNextAsync())
                {
                }
            }));

            var state = new TestCallState();
            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            var record = new SinkRecord();
            Task call = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8), 0, state), requestPipe.Reader, responsePipe.Writer, new SharedSink(record)).AsTask();
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(1));
            await Task.Yield();
            state.Cancel();
            await call;
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.Cancelled));
            Assert.That(record.CompleteCount, Is.EqualTo(1));
        }

        [Test]
        public async Task StalledStreamWaitsForCreditWhileAnotherStreamCompletes()
        {
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, byte[], IntCodec, BytesCodec>(Path, default, default, async (count, writer, ct) =>
            {
                for (int i = 0; i < count; i++)
                {
                    await writer.WriteAsync(new byte[100]);
                }
            }));

            var stalled = new TestCallState(HostPolicy.Credit, creditLimit: 150);
            var stalledRecord = new SinkRecord();
            var stalledResponse = new Pipe();
            var stalledRequest = new Pipe();
            await stalledRequest.Writer.WriteAsync(Frames.IntFrame(3));
            stalledRequest.Writer.Complete();
            Task stalledCall = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8), 0, stalled), stalledRequest.Reader, stalledResponse.Writer, new SharedSink(stalledRecord)).AsTask();

            Assert.That(stalled.WaitingForCredit, Is.True, "the second write exceeds the credit");
            Assert.That(stalledCall.IsCompleted, Is.False);

            Outcome live = await RunAsync(processor, Frames.IntFrame(3), new TestCallState());
            Assert.That(live.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(live.Responses, Has.Count.EqualTo(3));
            Assert.That(stalledCall.IsCompleted, Is.False);

            while (!stalledCall.IsCompleted)
            {
                stalled.ReleaseCredit();
                await Task.Delay(1);
            }

            await stalledCall;
            Assert.That(stalledRecord.LastStatus.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(stalled.MessagesWritten, Is.EqualTo(3));
        }

        [Test]
        public async Task CoreNeverFlushesNorAsksForUnflushedBytes()
        {
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (count, writer, ct) =>
            {
                for (int i = 0; i < count; i++)
                {
                    await writer.WriteAsync(i);
                }
            }));

            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            var counting = new CountingWriter(responsePipe.Writer);
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(4));
            requestPipe.Writer.Complete();
            var record = new SinkRecord();
            var state = new TestCallState();

            await processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8), 0, state), requestPipe.Reader, counting, new SharedSink(record));

            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(counting.Flushes, Is.EqualTo(0));
            Assert.That(state.MessagesWritten, Is.EqualTo(4));
        }

        [Test]
        public async Task HostHookRunsOncePerMessageOnTheWritingThread()
        {
            var writerThreads = new List<int>();
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (count, writer, ct) =>
            {
                for (int i = 0; i < count; i++)
                {
                    await Task.Yield();
                    writerThreads.Add(Environment.CurrentManagedThreadId);
                    await writer.WriteAsync(i);
                }
            }));

            var state = new TestCallState();
            Outcome outcome = await RunAsync(processor, Frames.IntFrame(6), state);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(state.HookThreads, Is.EqualTo(writerThreads));
        }

        [Test]
        public async Task OverlappingWritesAreRefused()
        {
            bool refused = false;
            TestCallState? state = null;
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (x, writer, ct) =>
            {
                await writer.WriteAsync(1);
                ValueTask pending = writer.WriteAsync(2);
                Assert.That(pending.IsCompleted, Is.False, "the second message exceeds the credit");
                try
                {
                    await writer.WriteAsync(3);
                }
                catch (InvalidOperationException)
                {
                    refused = true;
                }

                state!.ReleaseCredit();
                await pending;
            }));

            state = new TestCallState(HostPolicy.Credit, creditLimit: 10);
            Outcome outcome = await RunAsync(processor, Frames.IntFrame(0), state);
            Assert.That(refused, Is.True);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(outcome.Responses.Select(Int), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public async Task WriteFromAnotherThreadDuringAWriteIsRefused()
        {
            bool refused = false;
            TrappedWriter? trapped = null;
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (x, writer, ct) =>
            {
                trapped!.BlockNextGetSpan = true;
                Task first = Task.Run(() => writer.WriteAsync(1).AsTask());
                Assert.That(trapped.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first write is inside GetSpan");
                try
                {
                    await writer.WriteAsync(2);
                }
                catch (InvalidOperationException)
                {
                    refused = true;
                }

                trapped.Release.Set();
                await first;
                await writer.WriteAsync(3);
            }));

            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            trapped = new TrappedWriter(responsePipe.Writer);
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(0));
            requestPipe.Writer.Complete();
            var record = new SinkRecord();
            await processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, trapped, new SharedSink(record));
            await responsePipe.Writer.FlushAsync();
            responsePipe.Writer.Complete();

            Assert.That(refused, Is.True);
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
            Assert.That((await ReadFramesAsync(responsePipe.Reader)).Select(Int), Is.EqualTo(new[] { 1, 3 }));
        }

        [Test]
        public async Task OverlappingReadsAreRefused()
        {
            bool refused = false;
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
            {
                ValueTask<bool> first = reader.MoveNextAsync();
                Assert.That(first.IsCompleted, Is.False, "nothing has been sent yet");
                try
                {
                    await reader.MoveNextAsync();
                }
                catch (InvalidOperationException)
                {
                    refused = true;
                }

                Assert.That(await first, Is.True);
                await writer.WriteAsync(reader.Current);
                Assert.That(await reader.MoveNextAsync(), Is.False);
            }));

            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            var record = new SinkRecord();
            Task call = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, responsePipe.Writer, new SharedSink(record)).AsTask();
            await Task.Delay(20);
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(4));
            requestPipe.Writer.Complete();
            await call;

            Assert.That(refused, Is.True);
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
        }

        private static GrpcProcessor SwallowingDuplex(Action<Exception> seen) => Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
        {
            try
            {
                while (await reader.MoveNextAsync())
                {
                    await writer.WriteAsync(reader.Current);
                }
            }
            catch (Exception ex)
            {
                seen(ex);
            }
        }));

        [Test]
        public async Task CompletedReadStaysClaimedUntilItsResultIsTaken()
        {
            bool refused = false;
            var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
            {
                ValueTask<bool> first = reader.MoveNextAsync();
                Assert.That(first.IsCompleted, Is.False, "nothing has been sent yet");
                long until = Environment.TickCount64 + 5000;
                while (!first.IsCompleted && Environment.TickCount64 < until)
                {
                    await Task.Delay(1);
                }

                // Completed but not yet taken: a second read must not reset the source under it.
                Assert.That(first.IsCompleted, Is.True);
                try
                {
                    _ = reader.MoveNextAsync();
                }
                catch (InvalidOperationException)
                {
                    refused = true;
                }

                if (!refused)
                {
                    taken.SetResult();
                    return;
                }

                Assert.That(await first, Is.True);
                Assert.That(reader.Current, Is.EqualTo(4));
                taken.SetResult();
                Assert.That(await reader.MoveNextAsync(), Is.False);
            }));

            var requestPipe = new Pipe();
            var record = new SinkRecord();
            Task call = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, new Pipe().Writer, new SharedSink(record)).AsTask();
            await Task.Delay(20);
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(4));
            await taken.Task.WaitAsync(TimeSpan.FromSeconds(30));
            requestPipe.Writer.Complete();
            await call.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(refused, Is.True);
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RequestPipeThrowingSynchronouslyEndsTheCallInternal(bool inTryRead)
        {
            Exception? seen = null;
            GrpcProcessor processor = SwallowingDuplex(ex => seen = ex);
            var record = new SinkRecord();
            await processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), new ThrowingReader(inTryRead), new Pipe().Writer, new SharedSink(record));
            Assert.That(seen, Is.InstanceOf<IOException>());
            Assert.That(seen!.Message, Is.EqualTo(inTryRead ? "TryRead failed." : "ReadAsync failed."));
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.Internal));
        }

        [Test]
        public async Task RequestPipeFailureEndsTheCallInternalEvenWhenSwallowed()
        {
            // Failed before the call starts: the first read finds it.
            Exception? seen = null;
            GrpcProcessor processor = SwallowingDuplex(ex => seen = ex);
            var requestPipe = new Pipe();
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(1));
            requestPipe.Writer.Complete(new IOException("The client reset the stream."));
            var record = new SinkRecord();
            await processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, new Pipe().Writer, new SharedSink(record));
            Assert.That(seen, Is.InstanceOf<IOException>());
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.Internal));

            // Failed while a read is pending.
            seen = null;
            requestPipe = new Pipe();
            record = new SinkRecord();
            Task call = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, new Pipe().Writer, new SharedSink(record)).AsTask();
            await Task.Delay(20);
            requestPipe.Writer.Complete(new IOException("The client reset the stream."));
            await call;
            Assert.That(seen, Is.InstanceOf<IOException>());
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.Internal));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResponsePipeFailureEndsTheCallInternalEvenWhenSwallowed(bool failInAdvance)
        {
            Exception? seen = null;
            GrpcProcessor processor = SwallowingDuplex(ex => seen = ex);
            var requestPipe = new Pipe();
            await requestPipe.Writer.WriteAsync(Concat(Frames.IntFrame(1), Frames.IntFrame(2)));
            requestPipe.Writer.Complete();
            var trapped = new TrappedWriter(new Pipe().Writer) { FailGetSpan = !failInAdvance, FailAdvance = failInAdvance };
            var record = new SinkRecord();

            await processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8)), requestPipe.Reader, trapped, new SharedSink(record));

            Assert.That(seen, Is.InstanceOf<IOException>());
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.Internal));
        }

        [Test]
        public async Task ReadLeftPendingAtTheEndOfACallIsNotReused()
        {
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, (reader, writer, ct) =>
            {
                _ = reader.MoveNextAsync();
                return default;
            }));

            var state = new TestCallState();
            var requestPipe = new Pipe();
            var record = new SinkRecord();
            await processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8), 0, state), requestPipe.Reader, new Pipe().Writer, new SharedSink(record));
            Assert.That(record.LastStatus.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(state.ReaderCache, Is.Null, "a reader with a read in flight is dropped");

            // The abandoned read completes later without disturbing anything.
            await requestPipe.Writer.WriteAsync(Frames.IntFrame(1));
            requestPipe.Writer.Complete();
            await Task.Delay(20);
            Outcome next = await RunAsync(processor, Array.Empty<byte>(), state);
            Assert.That(next.Status.Code, Is.EqualTo(StatusCode.OK));
        }

        [TestCase(HostPolicy.ThrowSync)]
        [TestCase(HostPolicy.FaultAsync)]
        public async Task HostFailureAfterAWriteEndsTheCallInternal(HostPolicy policy)
        {
            bool sawFailure = false;
            GrpcProcessor processor = Build(b => b.AddServerStreaming<int, int, IntCodec, IntCodec>(Path, default, default, async (x, writer, ct) =>
            {
                try
                {
                    await writer.WriteAsync(1);
                }
                catch (IOException)
                {
                    sawFailure = true;
                }
            }));

            Outcome outcome = await RunAsync(processor, Frames.IntFrame(0), new TestCallState(policy));
            Assert.That(sawFailure, Is.True);
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.Internal));
        }

        [Test]
        public async Task EncodeFailureMidStreamEndsTheCallAndCommitsNothingOfThatMessage()
        {
            GrpcProcessor processor = Build(b => b.AddServerStreaming<byte[], byte[], BytesCodec, FaultyCodec>(Path, default, new FaultyCodec(Fault.EncodeThrows), async (x, writer, ct) =>
            {
                try
                {
                    await writer.WriteAsync(new byte[] { 1, 2, 3 });
                }
                catch (GrpcException)
                {
                }
            }));

            Outcome outcome = await RunAsync(processor, Frames.Frame(new byte[1]), new TestCallState());
            Assert.That(outcome.Status.Code, Is.EqualTo(StatusCode.Internal));
            Assert.That(outcome.Responses, Is.Empty);
        }

        [Test]
        public async Task ReaderAndWriterAreReusedThroughTheStateWithNothingCarriedOver()
        {
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, async (reader, writer, ct) =>
            {
                while (await reader.MoveNextAsync())
                {
                    await writer.WriteAsync(reader.Current);
                }
            }));

            var state = new TestCallState();
            Outcome faulted = await RunAsync(processor, Frames.Frame(new byte[] { 1 }, flag: 2), state);
            Assert.That(faulted.Status.Code, Is.EqualTo(StatusCode.Internal));
            object? reader = state.ReaderCache;
            object? writer = state.WriterCache;
            Assert.That(reader, Is.Not.Null);

            Outcome next = await RunAsync(processor, Concat(Frames.IntFrame(5), Frames.IntFrame(6)), state);
            Assert.That(next.Status.Code, Is.EqualTo(StatusCode.OK), "the first call's fault does not carry over");
            Assert.That(next.Responses.Select(Int), Is.EqualTo(new[] { 5, 6 }));
            Assert.That(state.ReaderCache, Is.SameAs(reader));
            Assert.That(state.WriterCache, Is.SameAs(writer));
        }

        [Test]
        public async Task HandlesKeptPastTheirCallThrowEvenAfterReuse()
        {
            var kept = new List<(MessageReader<int> Reader, MessageWriter<int> Writer)>();
            GrpcProcessor processor = Build(b => b.AddDuplex<int, int, IntCodec, IntCodec>(Path, default, default, (reader, writer, ct) =>
            {
                kept.Add((reader, writer));
                return default;
            }));

            var state = new TestCallState();
            await RunAsync(processor, Array.Empty<byte>(), state);
            Assert.Throws<InvalidOperationException>(() => kept[0].Reader.MoveNextAsync());
            Assert.Throws<InvalidOperationException>(() => kept[0].Writer.WriteAsync(1));

            // A second call reuses the same reader and writer; the first call's handles still refuse.
            Task second = processor.ProcessAsync(new CallHead(processor.Resolve("/test.Stream/Call"u8), 0, state), new Pipe().Reader, new Pipe().Writer, new SharedSink(new SinkRecord())).AsTask();
            await second;
            Assert.Throws<InvalidOperationException>(() => kept[0].Reader.MoveNextAsync());
            Assert.Throws<InvalidOperationException>(() => _ = kept[0].Reader.Current);
            Assert.Throws<InvalidOperationException>(() => default(MessageReader<int>).MoveNextAsync());
        }
    }
}
