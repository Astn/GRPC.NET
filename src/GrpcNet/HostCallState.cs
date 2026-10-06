using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace GrpcNet
{
    /// <summary>
    /// The host's per-stream state, pooled by the host per connection along with the stream, and passed in
    /// <see cref="CallHead.State"/>. It gives the core the call's cancellation, takes the host's flush and flow-control
    /// decision after each response message, and keeps the stream's message reader and writer for reuse by the next call on it.
    /// </summary>
    public abstract class HostCallState
    {
        /// <summary>Cancelled when the stream is reset, the connection drops or the host expires the deadline.</summary>
        public abstract CancellationToken Cancellation { get; }

        /// <summary>
        /// Called by the core on the writing thread after it has advanced one framed response message of
        /// <paramref name="framedBytes"/> bytes into <paramref name="output"/>. The host decides here whether to flush, to keep
        /// buffering, or to wait until its own accounting of bytes actually sent is back under its per-stream bound; the core
        /// never flushes. The core does not write to <paramref name="output"/> again until the returned task completes.
        /// If this throws or the task faults, the message may be partly published: the call ends <see cref="StatusCode.Internal"/>
        /// and the host must reset the stream if it cannot send trailers.
        /// </summary>
        protected abstract ValueTask OnMessageWritten(PipeWriter output, int framedBytes);

        internal ValueTask MessageWritten(PipeWriter output, int framedBytes) => OnMessageWritten(output, framedBytes);

        // The reader and writer of the stream's last call, reused by the next call when its message and codec types match.
        internal object? ReaderCache;

        internal object? WriterCache;
    }
}
