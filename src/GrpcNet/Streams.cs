using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace GrpcNet
{
    /// <summary>
    /// Reads a stream's request messages. A handle for one call: it throws once that call has ended, even if the host has
    /// reused the underlying reader for a later call.
    /// </summary>
    public readonly struct MessageReader<T>
    {
        private readonly ReaderCore<T>? _core;
        private readonly int _stamp;

        internal MessageReader(ReaderCore<T> core, int stamp)
        {
            _core = core;
            _stamp = stamp;
        }

        /// <summary>
        /// Moves to the next request message. Returns false at the end of the request stream. Throws a
        /// <see cref="GrpcException"/> when the request stream breaks the protocol; the call then ends with that status even if
        /// the exception is caught.
        /// </summary>
        public ValueTask<bool> MoveNextAsync() => Core.MoveNextAsync(_stamp);

        /// <summary>The current message. Messages are owned: a message stays valid after later reads.</summary>
        public ref readonly T Current => ref Core.CurrentRef(_stamp);

        private ReaderCore<T> Core => _core ?? throw new InvalidOperationException("This message reader is not attached to a call.");
    }

    /// <summary>
    /// Writes a stream's response messages. A handle for one call: it throws once that call has ended, even if the host has
    /// reused the underlying writer for a later call.
    /// </summary>
    public readonly struct MessageWriter<T>
    {
        private readonly WriterCore<T>? _core;
        private readonly int _stamp;

        internal MessageWriter(WriterCore<T> core, int stamp)
        {
            _core = core;
            _stamp = stamp;
        }

        /// <summary>
        /// Sizes, encodes and commits one response message, then lets the host decide on flushing and flow control; the core
        /// never flushes. Await each write before starting the next: an overlapping write throws.
        /// </summary>
        public ValueTask WriteAsync(in T message) => Core.WriteAsync(_stamp, in message);

        private WriterCore<T> Core => _core ?? throw new InvalidOperationException("This message writer is not attached to a call.");
    }

    /// <summary>
    /// The first terminal fault of a call, shared by its reader and writer. Once latched, the call ends with it whatever the
    /// handler returns.
    /// </summary>
    internal sealed class CallLatch
    {
        private readonly object _gate = new object();
        private volatile bool _hasFault;
        private GrpcStatus _fault;
        private string? _message;

        internal bool HasFault => _hasFault;

        internal GrpcStatus Fault => _fault;

        internal void Reset()
        {
            _hasFault = false;
            _fault = default;
            _message = null;
        }

        /// <summary>Records <paramref name="code"/> unless a fault is already latched, and returns the exception for the latched fault.</summary>
        internal GrpcException Latch(StatusCode code, string message, GrpcProcessorOptions options)
        {
            lock (_gate)
            {
                if (!_hasFault)
                {
                    _fault = options.DetailedErrors ? GrpcStatus.FromString(code, message) : new GrpcStatus(code);
                    _message = message;
                    _hasFault = true;
                }

                return Exception();
            }
        }

        internal GrpcException Latch(in GrpcStatus status)
        {
            lock (_gate)
            {
                if (!_hasFault)
                {
                    _fault = status;
                    _message = null;
                    _hasFault = true;
                }

                return Exception();
            }
        }

        internal GrpcException Exception() => _message != null ? new GrpcException(_fault.Code, _message) : new GrpcException(_fault.Code);
    }

    /// <summary>Frames and decodes request messages from a pipe. Reused across the calls of a stream.</summary>
    internal abstract class ReaderCore<T> : IValueTaskSource<bool>
    {
        private enum Step
        {
            Message,
            End,
            NeedMore,
            Fault,
            Transport,
            Canceled,
        }

        private readonly Action _onReadCompleted;
        private ManualResetValueTaskSourceCore<bool> _source;
        private ConfiguredValueTaskAwaitable<ReadResult>.ConfiguredValueTaskAwaiter _readAwaiter;
        private PipeReader _input = null!;
        private GrpcProcessorOptions _options = null!;
        private CancellationToken _cancellation;
        private T _current = default!;
        private Exception? _transportError;
        private int _stamp;
        private int _busy;
        private bool _active;
        private bool _ended;

        protected ReaderCore()
        {
            _onReadCompleted = OnReadCompleted;
        }

        internal CallLatch Latch { get; } = new CallLatch();

        /// <summary>Attaches the reader to a call and returns the call's stamp.</summary>
        internal int Start(PipeReader input, in CallHead head, GrpcProcessorOptions options)
        {
            _input = input;
            _options = options;
            _cancellation = head.Cancellation;
            _current = default!;
            _transportError = null;
            _ended = false;
            _active = true;
            Latch.Reset();
            return ++_stamp;
        }

        internal void End()
        {
            _active = false;
            _current = default!;
            if (IsIdle)
            {
                _input = null!;
            }
        }

        /// <summary>False while a read is still pending, such as one the handler started and never awaited.</summary>
        internal bool IsIdle => Volatile.Read(ref _busy) == 0;

        internal ref readonly T CurrentRef(int stamp)
        {
            Check(stamp);
            return ref _current;
        }

        internal ValueTask<bool> MoveNextAsync(int stamp)
        {
            Check(stamp);
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("MoveNextAsync was called before the previous read completed.");
            }

            bool pending = false;
            try
            {
                if (Latch.HasFault)
                {
                    throw Latch.Exception();
                }

                if (_ended)
                {
                    return new ValueTask<bool>(false);
                }

                Step step;
                try
                {
                    step = _input.TryRead(out ReadResult buffered) ? Consume(in buffered) : Step.NeedMore;
                }
                catch (Exception ex)
                {
                    step = Transport(ex);
                }

                switch (step)
                {
                    case Step.Message:
                        return new ValueTask<bool>(true);
                    case Step.End:
                        return new ValueTask<bool>(false);
                    case Step.Fault:
                        throw Latch.Exception();
                    case Step.Transport:
                        ExceptionDispatchInfo.Throw(_transportError!);
                        break;
                    case Step.Canceled:
                        throw new OperationCanceledException(_cancellation);
                }

                // Nothing, or only part of a frame, is buffered: wait for more data.
                _source.Reset();
                pending = true;
                ReadUntilDone();
                return new ValueTask<bool>(this, _source.Version);
            }
            finally
            {
                if (!pending)
                {
                    Volatile.Write(ref _busy, 0);
                }
            }
        }

        /// <summary>Reads the one request message of a unary or server-streaming call, and the end of the stream after it.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        internal async ValueTask<T> ReadSingleAsync(int stamp)
        {
            if (!await MoveNextAsync(stamp).ConfigureAwait(false))
            {
                throw Latch.Latch(StatusCode.Internal, "The request stream contains no message.", _options);
            }

            T message = _current;
            if (await MoveNextAsync(stamp).ConfigureAwait(false))
            {
                throw Latch.Latch(StatusCode.Internal, "The request stream contains more than one message.", _options);
            }

            return message;
        }

        private protected abstract T DecodeSpan(ReadOnlySpan<byte> payload);

        private void Check(int stamp)
        {
            if (stamp != _stamp || !_active)
            {
                throw new InvalidOperationException("The message reader belongs to a call that has ended.");
            }
        }

        private void ReadUntilDone()
        {
            while (true)
            {
                Step step;
                try
                {
                    ValueTask<ReadResult> read = _input.ReadAsync(_cancellation);
                    if (!read.IsCompleted)
                    {
                        _readAwaiter = read.ConfigureAwait(false).GetAwaiter();
                        _readAwaiter.UnsafeOnCompleted(_onReadCompleted);
                        return;
                    }

                    ReadResult result = read.GetAwaiter().GetResult();
                    step = Consume(in result);
                }
                catch (Exception ex)
                {
                    step = Transport(ex);
                }

                if (Complete(step))
                {
                    return;
                }
            }
        }

        private void OnReadCompleted()
        {
            Step step;
            try
            {
                ReadResult result = _readAwaiter.GetResult();
                step = Consume(in result);
            }
            catch (Exception ex)
            {
                step = Transport(ex);
            }

            if (!Complete(step))
            {
                ReadUntilDone();
            }
        }

        // Completes the pending MoveNextAsync; returns false when more data is needed. The reader stays claimed until the
        // awaiter takes the result, so no other read can reset the source between completion and consumption.
        private bool Complete(Step step)
        {
            if (step == Step.NeedMore)
            {
                return false;
            }

            switch (step)
            {
                case Step.Message:
                    _source.SetResult(true);
                    break;
                case Step.End:
                    _source.SetResult(false);
                    break;
                case Step.Fault:
                    _source.SetException(Latch.Exception());
                    break;
                case Step.Transport:
                    _source.SetException(_transportError!);
                    break;
                default:
                    _source.SetException(new OperationCanceledException(_cancellation));
                    break;
            }

            return true;
        }

        // A failure of the pipe itself. The call's own cancellation is reported as cancellation; any other failure is
        // latched, so the call ends INTERNAL even if the handler catches it.
        private Step Transport(Exception ex)
        {
            if (ex is OperationCanceledException && _cancellation.IsCancellationRequested)
            {
                return Step.Canceled;
            }

            Latch.Latch(StatusCode.Internal, "The request stream failed.", _options);
            _transportError = ex;
            return Step.Transport;
        }

        private Step Consume(in ReadResult result)
        {
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (result.IsCanceled)
            {
                _input.AdvanceTo(buffer.Start);
                return Step.Canceled;
            }

            if (!MessageFraming.TryReadPrefix(in buffer, out _, out uint length))
            {
                if (!result.IsCompleted)
                {
                    _input.AdvanceTo(buffer.Start, buffer.End);
                    return Step.NeedMore;
                }

                _input.AdvanceTo(buffer.End);
                if (buffer.IsEmpty)
                {
                    _ended = true;
                    return Step.End;
                }

                return Fault(StatusCode.Internal, "The request stream ends inside a message prefix.");
            }

            byte flag = buffer.FirstSpan.Length > 0 ? buffer.FirstSpan[0] : FirstByte(in buffer);
            if (flag != 0 || length > (uint)_options.MaxReceiveMessageSize)
            {
                _input.AdvanceTo(buffer.Start, buffer.End);
                return flag > 1 ? Fault(StatusCode.Internal, "A message prefix has an invalid compressed flag.")
                    : flag == 1 ? Fault(StatusCode.Unimplemented, "Compressed messages are not supported.")
                    : Fault(StatusCode.ResourceExhausted, "A request message is larger than the receive limit.");
            }

            if (buffer.Length - MessageFraming.PrefixLength < length)
            {
                if (!result.IsCompleted)
                {
                    _input.AdvanceTo(buffer.Start, buffer.End);
                    return Step.NeedMore;
                }

                _input.AdvanceTo(buffer.End);
                return Fault(StatusCode.Internal, "The request stream ends inside a message.");
            }

            ReadOnlySequence<byte> payload = buffer.Slice(MessageFraming.PrefixLength, length);
            T message;
            try
            {
                message = Decode(in payload);
            }
            catch (Exception ex)
            {
                _input.AdvanceTo(buffer.Start, buffer.End);
                Latch.Latch(StatusMapping.FromCodecException(ex, _options));
                return Step.Fault;
            }

            // Messages are owned, so the bytes can be released as soon as the message is decoded.
            _current = message;
            _input.AdvanceTo(payload.End);
            return Step.Message;
        }

        private T Decode(in ReadOnlySequence<byte> payload)
        {
            if (payload.IsSingleSegment)
            {
                return DecodeSpan(payload.FirstSpan);
            }

            int size = (int)payload.Length;
            byte[] buffer = PayloadScratch.Acquire(size, out bool rented);
            try
            {
                payload.CopyTo(buffer);
                return DecodeSpan(buffer.AsSpan(0, size));
            }
            finally
            {
                PayloadScratch.Release(buffer, rented);
            }
        }

        private Step Fault(StatusCode code, string message)
        {
            Latch.Latch(code, message, _options);
            return Step.Fault;
        }

        private static byte FirstByte(in ReadOnlySequence<byte> buffer)
        {
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                if (!segment.IsEmpty)
                {
                    return segment.Span[0];
                }
            }

            return 0;
        }

        bool IValueTaskSource<bool>.GetResult(short token)
        {
            // A stale token or a result not yet published throws without touching the claim.
            if (token != _source.Version || _source.GetStatus(token) == ValueTaskSourceStatus.Pending)
            {
                return _source.GetResult(token);
            }

            try
            {
                return _source.GetResult(token);
            }
            finally
            {
                // The result is consumed: release the reader, so the continuation can read again.
                Volatile.Write(ref _busy, 0);
            }
        }

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _source.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }

    internal sealed class CodecReader<T, TCodec> : ReaderCore<T>
        where TCodec : struct, IMessageCodec<T>
    {
        private TCodec _codec;

        internal int Start(PipeReader input, in CallHead head, GrpcProcessorOptions options, TCodec codec)
        {
            // A fresh copy of the codec as bound, for this call only.
            _codec = codec;
            return Start(input, in head, options);
        }

        private protected override T DecodeSpan(ReadOnlySpan<byte> payload) => _codec.Decode(payload);
    }

    /// <summary>Frames and commits response messages to a pipe. Reused across the calls of a stream.</summary>
    internal abstract class WriterCore<T>
    {
        private PipeWriter _output = null!;
        private HostCallState? _state;
        private GrpcProcessorOptions _options = null!;
        private CallLatch _latch = null!;
        private CancellationToken _cancellation;
        private long _deadlineTicks;
        private int _stamp;
        private int _busy;
        private bool _active;

        internal int Start(PipeWriter output, in CallHead head, GrpcProcessorOptions options, CallLatch latch)
        {
            _output = output;
            _state = head.State;
            _options = options;
            _latch = latch;
            _cancellation = head.Cancellation;
            _deadlineTicks = head.DeadlineTicks;
            _active = true;
            return ++_stamp;
        }

        internal void End()
        {
            _active = false;
            if (IsIdle)
            {
                _output = null!;
                _state = null;
                _latch = null!;
            }
        }

        /// <summary>False while the host's task for a write is still pending, such as one the handler never awaited.</summary>
        internal bool IsIdle => Volatile.Read(ref _busy) == 0;

        internal ValueTask WriteAsync(int stamp, in T message)
        {
            if (stamp != _stamp || !_active)
            {
                throw new InvalidOperationException("The message writer belongs to a call that has ended.");
            }

            // The pipe is single-writer: claim it, so a write that overlaps another, from any thread, is refused.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("WriteAsync was called before the previous write completed.");
            }

            bool handedOff = false;
            try
            {
                if (_latch.HasFault)
                {
                    throw _latch.Exception();
                }

                // Checked before anything is committed, so no message goes out after the deadline.
                if (_deadlineTicks != 0 && Environment.TickCount64 >= _deadlineTicks)
                {
                    throw _latch.Latch(StatusCode.DeadlineExceeded, "The deadline passed before the message was written.", _options);
                }

                _cancellation.ThrowIfCancellationRequested();

                GrpcStatus written;
                int framedBytes;
                try
                {
                    written = WriteFrame(in message, _output, _options, out framedBytes);
                }
                catch (Exception)
                {
                    // The pipe failed, possibly after part of a frame was committed.
                    _latch.Latch(new GrpcStatus(StatusCode.Internal));
                    throw;
                }

                if (written.Code != StatusCode.OK)
                {
                    throw _latch.Latch(in written);
                }

                if (_state == null)
                {
                    return default;
                }

                ValueTask host;
                try
                {
                    host = _state.MessageWritten(_output, framedBytes);
                }
                catch (Exception)
                {
                    _latch.Latch(new GrpcStatus(StatusCode.Internal));
                    throw;
                }

                if (host.IsCompletedSuccessfully)
                {
                    host.GetAwaiter().GetResult();
                    return default;
                }

                handedOff = true;
                return AwaitHostAsync(host);
            }
            finally
            {
                if (!handedOff)
                {
                    Volatile.Write(ref _busy, 0);
                }
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask AwaitHostAsync(ValueTask host)
        {
            try
            {
                await host.ConfigureAwait(false);
            }
            catch (Exception)
            {
                _latch.Latch(new GrpcStatus(StatusCode.Internal));
                throw;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        private protected abstract GrpcStatus WriteFrame(in T message, PipeWriter output, GrpcProcessorOptions options, out int framedBytes);
    }

    internal sealed class CodecWriter<T, TCodec> : WriterCore<T>
        where TCodec : struct, IMessageCodec<T>
    {
        private TCodec _codec;

        internal int Start(PipeWriter output, in CallHead head, GrpcProcessorOptions options, CallLatch latch, TCodec codec)
        {
            // A fresh copy of the codec as bound, for this call only.
            _codec = codec;
            return Start(output, in head, options, latch);
        }

        private protected override GrpcStatus WriteFrame(in T message, PipeWriter output, GrpcProcessorOptions options, out int framedBytes)
            => ResponseWriter.Write(ref _codec, in message, output, options, out framedBytes);
    }
}
