using System;

namespace GrpcNet
{
    /// <summary>
    /// Decodes and encodes one message type. Implementations are structs, bound as a type parameter at registration, so the
    /// JIT specializes every call with no interface dispatch.
    /// </summary>
    /// <remarks>
    /// Calls run concurrently and may nest. Each call works on its own copy of the codec as bound, and the copy passed to
    /// <see cref="Size"/> is the one passed to <see cref="Encode"/>, so the struct's own fields are private to the call.
    /// Objects the struct refers to are shared by every copy: they must be immutable or thread-safe.
    /// </remarks>
    /// <typeparam name="T">The message type.</typeparam>
    public interface IMessageCodec<T>
    {
        /// <summary>Decodes a message from exactly <paramref name="payload"/>. Throws <see cref="GrpcCodecException"/> on malformed input.</summary>
        T Decode(ReadOnlySpan<byte> payload);

        /// <summary>Returns the exact encoded size of <paramref name="message"/> in bytes.</summary>
        int Size(in T message);

        /// <summary>Encodes <paramref name="message"/> into <paramref name="destination"/>, whose length is exactly <paramref name="size"/>.</summary>
        void Encode(in T message, int size, Span<byte> destination);
    }
}
