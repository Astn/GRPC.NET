using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using GrpcNet.Protobuf.Tests.Messages;
using NUnit.Framework;

namespace GrpcNet.Protobuf.Tests
{
    public class InvokerTagTests
    {
        private struct AddTag
        {
        }

        private struct NothingTag
        {
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

        /// <summary>A struct invoker with its handler in code, as an interceptor or an attribute class would supply.</summary>
        private readonly struct DoublingInvoker : IUnaryInvoker<Scalar, Scalar>, IAsyncUnaryInvoker<Scalar, Scalar>
        {
            public Scalar Invoke(Scalar request) => new Scalar { A = request.A * 2 };

            public ValueTask<Scalar> InvokeAsync(Scalar request, CancellationToken cancellation) => new ValueTask<Scalar>(new Scalar { A = request.A * 3 });
        }

        private static Type EntryTypeOf(GrpcProcessor processor, GrpcMethod method)
            => processor.EntryOf(processor.Resolve(method.PathUtf8.Span))!.GetType();

        private static Type TagOf(GrpcProcessor processor, GrpcMethod method)
            => EntryTypeOf(processor, method).GetGenericArguments().Last().GetGenericArguments().Last();

        [Test]
        public void MethodObjectsFromTheConstructorShareOneTag()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .Bind(Calculator.Add, r => r)
                .Bind(Calculator.Nothing, r => r)
                .Build();
            Assert.That(TagOf(processor, Calculator.Add), Is.EqualTo(typeof(UntaggedMethod)));
            Assert.That(TagOf(processor, Calculator.Nothing), Is.EqualTo(typeof(UntaggedMethod)));
        }

        [Test]
        public void MethodObjectsFromCreateBindWithTheirTag()
        {
            UnaryMethod<Scalar, Scalar> add = UnaryMethod<Scalar, Scalar>.Create<AddTag>(TestReflection.Descriptor, "test.Calculator", "Add");
            UnaryMethod<Messages.Empty, Messages.Empty> nothing = UnaryMethod<Messages.Empty, Messages.Empty>.Create<NothingTag>(TestReflection.Descriptor, "test.Calculator", "Nothing");
            ServerStreamingMethod<Scalar, Scalar> watch = ServerStreamingMethod<Scalar, Scalar>.Create<AddTag>(TestReflection.Descriptor, "test.Calculator", "Watch");
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .Bind(add, r => r)
                .BindAsync(nothing, r => new ValueTask<Messages.Empty>(r))
                .Bind(watch, (r, w, ct) => default)
                .Build();
            Assert.That(TagOf(processor, add), Is.EqualTo(typeof(AddTag)));
            Assert.That(TagOf(processor, nothing), Is.EqualTo(typeof(NothingTag)));
            Assert.That(TagOf(processor, watch), Is.EqualTo(typeof(AddTag)));
        }

        [Test]
        public void DelegateRegistrationsInTheCoreShareOneTag()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>>("/test.Calculator/Add", Codec(Scalar.Parser), Codec(Scalar.Parser), r => r)
                .Build();
            Assert.That(TagOf(processor, Calculator.Add).Name, Is.EqualTo("SharedTag"));
        }

        [Test]
        public async Task StructInvokerRunsOnTheDirectAndPipePaths()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>, DoublingInvoker>("/test.Calculator/Add", Codec(Scalar.Parser), Codec(Scalar.Parser), default)
                .AddAsyncUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>, DoublingInvoker>("/test.Other/Add", Codec(Scalar.Parser), Codec(Scalar.Parser), default)
                .Build();

            var sink = new NullSink();
            var output = new ArrayBufferWriter<byte>();
            GrpcStatus status = processor.Process(new CallHead(processor.Resolve("/test.Calculator/Add"u8)), Frame(new Scalar { A = 21 }), output, ref sink);
            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            Assert.That(Scalar.Parser.ParseFrom(output.WrittenSpan.Slice(5)).A, Is.EqualTo(42));

            Assert.That((await CallAsync(processor, "/test.Calculator/Add", new Scalar { A = 5 })).A, Is.EqualTo(10));
            Assert.That(processor.IsSynchronousUnary(processor.Resolve("/test.Other/Add"u8)), Is.False);
            Assert.That((await CallAsync(processor, "/test.Other/Add", new Scalar { A = 5 })).A, Is.EqualTo(15));
        }

        [Test]
        public void InvokerRegistrationRejectsADuplicatePathAndLeavesTheBuilderAsItWas()
        {
            var builder = new GrpcProcessorBuilder()
                .AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>, DoublingInvoker>("/test.Calculator/Add", Codec(Scalar.Parser), Codec(Scalar.Parser), default);
            Assert.Throws<InvalidOperationException>(() => builder.AddAsyncUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>, DoublingInvoker>(
                "/test.Calculator/Add", Codec(Scalar.Parser), Codec(Scalar.Parser), default));
            Assert.Throws<ArgumentException>(() => builder.AddUnary<Scalar, Scalar, ProtobufCodec<Scalar>, ProtobufCodec<Scalar>, DoublingInvoker>(
                "no-slash", Codec(Scalar.Parser), Codec(Scalar.Parser), default));
            Assert.That(builder.Bindings.Select(b => b.Path), Is.EqualTo(new[] { "/test.Calculator/Add" }));
        }

        private static ProtobufCodec<T> Codec<T>(MessageParser<T> parser)
            where T : class, IMessage<T> => new ProtobufCodec<T>(parser);

        private static byte[] Frame(IMessage message)
        {
            byte[] payload = message.ToByteArray();
            var frame = new byte[5 + payload.Length];
            MessageFraming.WritePrefix(frame, payload.Length);
            payload.CopyTo(frame, 5);
            return frame;
        }

        private static async Task<Scalar> CallAsync(GrpcProcessor processor, string path, Scalar request)
        {
            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            await requestPipe.Writer.WriteAsync(Frame(request));
            requestPipe.Writer.Complete();
            await processor.ProcessAsync(new CallHead(processor.Resolve(System.Text.Encoding.UTF8.GetBytes(path))), requestPipe.Reader, responsePipe.Writer, new NullSink());
            await responsePipe.Writer.FlushAsync();
            responsePipe.Writer.Complete();
            ReadResult result = await responsePipe.Reader.ReadAsync();
            while (!result.IsCompleted)
            {
                responsePipe.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                result = await responsePipe.Reader.ReadAsync();
            }

            return Scalar.Parser.ParseFrom(result.Buffer.Slice(5));
        }
    }
}
