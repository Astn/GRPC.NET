namespace GrpcNet
{
    /// <summary>
    /// The boundary events a host carries back out of a call, in order: response headers (only when there are custom ones),
    /// then completion with the status and trailing metadata. Hosts implement this as a struct and pass it by reference,
    /// so the call is specialized with no interface dispatch.
    /// </summary>
    public interface ICallSink
    {
        /// <summary>Sends response headers before the first message. Called only when custom headers exist.</summary>
        void WriteHeaders(in MetadataView headers);

        /// <summary>Completes the call with <paramref name="status"/> and trailing metadata.</summary>
        void Complete(in GrpcStatus status, in MetadataView trailers);
    }

    /// <summary>
    /// A borrowed view of metadata, valid for the duration of the call that receives it. This milestone-1 step carries no
    /// entries; lookups arrive with the host's metadata support.
    /// </summary>
    public readonly ref struct MetadataView
    {
        /// <summary>A view with no entries.</summary>
        public static MetadataView Empty => default;

        /// <summary>Whether the view has no entries.</summary>
        public bool IsEmpty => true;
    }
}
