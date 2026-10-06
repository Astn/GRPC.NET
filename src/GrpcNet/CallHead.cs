using System;

namespace GrpcNet
{
    /// <summary>Everything a host knows when a call starts. No owned object is created per call.</summary>
    public readonly struct CallHead
    {
        /// <summary>Creates a call head.</summary>
        /// <param name="method">The method, from <see cref="GrpcProcessor.Resolve"/> or a route bound at registration.</param>
        /// <param name="deadlineTicks">The absolute deadline on the <see cref="Environment.TickCount64"/> scale (milliseconds), or 0 for none.</param>
        /// <param name="state">The host's per-stream state, or null. Synchronous unary calls do not need it.</param>
        public CallHead(MethodId method, long deadlineTicks = 0, HostCallState? state = null)
        {
            Method = method;
            DeadlineTicks = deadlineTicks;
            State = state;
        }

        /// <summary>The method being called.</summary>
        public MethodId Method { get; }

        /// <summary>The absolute deadline on the <see cref="Environment.TickCount64"/> scale, or 0 when the call has none.</summary>
        public long DeadlineTicks { get; }

        /// <summary>The host's per-stream state, or null.</summary>
        public HostCallState? State { get; }

        /// <summary>Whether the deadline has passed. False when the call has no deadline.</summary>
        public bool IsPastDeadline => DeadlineTicks != 0 && Environment.TickCount64 >= DeadlineTicks;

        internal System.Threading.CancellationToken Cancellation => State?.Cancellation ?? default;
    }

    /// <summary>Identifies a method in a <see cref="GrpcProcessor"/>'s method table.</summary>
    public readonly struct MethodId : IEquatable<MethodId>
    {
        private readonly int _indexPlusOne;

        internal MethodId(int index)
        {
            _indexPlusOne = index + 1;
        }

        /// <summary>No method: the path did not resolve. The host answers <see cref="StatusCode.Unimplemented"/>.</summary>
        public static MethodId None => default;

        /// <summary>Whether this identifies a method.</summary>
        public bool IsValid => _indexPlusOne != 0;

        internal int Index => _indexPlusOne - 1;

        /// <inheritdoc />
        public bool Equals(MethodId other) => _indexPlusOne == other._indexPlusOne;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is MethodId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => _indexPlusOne;

        /// <summary>Compares two method ids.</summary>
        public static bool operator ==(MethodId left, MethodId right) => left.Equals(right);

        /// <summary>Compares two method ids.</summary>
        public static bool operator !=(MethodId left, MethodId right) => !left.Equals(right);
    }

    /// <summary>The four gRPC call kinds. The wire does not carry the kind; the method's definition does.</summary>
    public enum MethodKind
    {
        /// <summary>One request message, one response message.</summary>
        Unary,
        /// <summary>Any number of request messages, one response message.</summary>
        ClientStreaming,
        /// <summary>One request message, any number of response messages.</summary>
        ServerStreaming,
        /// <summary>Any number of messages in each direction.</summary>
        DuplexStreaming,
    }
}
