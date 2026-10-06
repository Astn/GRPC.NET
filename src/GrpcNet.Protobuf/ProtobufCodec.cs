using System;
using Google.Protobuf;

namespace GrpcNet.Protobuf
{
    /// <summary>
    /// The Google.Protobuf codec: parses from the payload span, sizes with <c>CalculateSize</c> and writes into exactly that
    /// many bytes. It adds no wrapper or context object per call; the message and its fields allocate as Google.Protobuf
    /// allocates them.
    /// </summary>
    /// <typeparam name="T">A protoc-generated message type.</typeparam>
    public readonly struct ProtobufCodec<T> : IMessageCodec<T>
        where T : class, IMessage<T>
    {
        // MessageParser<T> is immutable once built, so every copy of the codec may share it.
        private readonly MessageParser<T> _parser;

        /// <summary>Creates a codec that parses with <paramref name="parser"/>, normally the message type's static <c>Parser</c>.</summary>
        public ProtobufCodec(MessageParser<T> parser)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <inheritdoc />
        public T Decode(ReadOnlySpan<byte> payload) => _parser.ParseFrom(payload);

        /// <inheritdoc />
        public int Size(in T message) => message.CalculateSize();

        /// <inheritdoc />
        public void Encode(in T message, int size, Span<byte> destination) => message.WriteTo(destination);
    }
}
