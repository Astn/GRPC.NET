using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using GrpcNet.Generator.EndToEnd.Shop;
using GrpcNet.Protobuf;
using NUnit.Framework;

namespace GrpcNet.Generator.EndToEnd.Tests
{
    /// <summary>Each generated method object binds its handlers into an entry compiled for that method alone.</summary>
    public class InvokerTests
    {
        private struct NullSink : ICallSink
        {
            public void WriteHeaders(in MetadataView headers)
            {
            }

            public void Complete(in GrpcStatus status, in MetadataView trailers)
            {
            }
        }

        // The entry's last type argument is the invoker; the invoker's last is the tag.
        private static Type TagOf(GrpcProcessor processor, GrpcMethod method)
        {
            Type entry = processor.EntryOf(processor.Resolve(method.PathUtf8.Span))!.GetType();
            Type invoker = entry.GetGenericArguments().Last();
            return invoker.GetGenericArguments().Last();
        }

        private static Type EntryTypeOf(GrpcProcessor processor, GrpcMethod method)
            => processor.EntryOf(processor.Resolve(method.PathUtf8.Span))!.GetType();

        [Test]
        public void EveryGeneratedMethodBindsWithATagOfItsOwn()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(new Shop.Shop.Binder
            {
                Get = Shop.Shop.Get.Sync(q => new Item()),
                Find = Shop.Shop.Find.Async(async (q, ct) => { await Task.Yield(); return new Item(); }),
                List = (q, w, ct) => default,
                Total = (r, ct) => new ValueTask<Item.Types.Price>(new Item.Types.Price()),
                Echo = (r, w, ct) => default,
                Ping = Shop.Shop.Ping.Async(async e => { await Task.Yield(); return e; }),
            }).Build();

            GrpcMethod[] methods = { Shop.Shop.Get, Shop.Shop.Find, Shop.Shop.List, Shop.Shop.Total, Shop.Shop.Echo, Shop.Shop.Ping };
            foreach (GrpcMethod method in methods)
            {
                Type tag = TagOf(processor, method);
                Assert.That((tag.DeclaringType?.Name, tag.Name), Is.EqualTo(("GrpcNetTags", method.Descriptor.Name)), method.Path);
                Assert.That(tag.IsValueType, Is.True);
            }

            Assert.That(methods.Select(m => EntryTypeOf(processor, m)).Distinct().Count(), Is.EqualTo(methods.Length));
        }

        [Test]
        public async Task MethodsWithTheSameTypesRunInDifferentEntriesOnBothPaths()
        {
            // Get and Find have the same request and response types, so without tags they would share one compiled entry.
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .Bind(Shop.Shop.Get, q => new Item { Name = "get" + q.Count })
                .Bind(Shop.Shop.Find, q => new Item { Name = "find" + q.Count })
                .Build();
            Assert.That(EntryTypeOf(processor, Shop.Shop.Get), Is.Not.EqualTo(EntryTypeOf(processor, Shop.Shop.Find)));
            Assert.That(processor.IsSynchronousUnary(processor.Resolve(Shop.Shop.Find.PathUtf8.Span)), Is.True);

            // The direct path.
            Assert.That(Process(processor, Shop.Shop.Get, new Query { Count = 1 }).Name, Is.EqualTo("get1"));
            Assert.That(Process(processor, Shop.Shop.Find, new Query { Count = 2 }).Name, Is.EqualTo("find2"));

            // The pipe path.
            Assert.That((await ProcessAsync(processor, Shop.Shop.Get, new Query { Count = 3 })).Name, Is.EqualTo("get3"));
            Assert.That((await ProcessAsync(processor, Shop.Shop.Find, new Query { Count = 4 })).Name, Is.EqualTo("find4"));

            // Asynchronous forms are tagged too: with and without the token.
            GrpcProcessor asynchronous = new GrpcProcessorBuilder()
                .BindAsync(Shop.Shop.Get, async (q, ct) => { await Task.Yield(); return new Item { Name = "aget" + q.Count }; })
                .BindAsync(Shop.Shop.Find, async q => { await Task.Yield(); return new Item { Name = "afind" + q.Count }; })
                .Build();
            Assert.That(TagOf(asynchronous, Shop.Shop.Get).Name, Is.EqualTo("Get"));
            Assert.That(TagOf(asynchronous, Shop.Shop.Find).Name, Is.EqualTo("Find"));
            Assert.That((await ProcessAsync(asynchronous, Shop.Shop.Get, new Query { Count = 5 })).Name, Is.EqualTo("aget5"));
            Assert.That((await ProcessAsync(asynchronous, Shop.Shop.Find, new Query { Count = 6 })).Name, Is.EqualTo("afind6"));
        }

        private static Item Process(GrpcProcessor processor, GrpcMethod method, Query request)
        {
            var sink = new NullSink();
            var output = new ArrayBufferWriter<byte>();
            GrpcStatus status = processor.Process(new CallHead(processor.Resolve(method.PathUtf8.Span)), Frame(request), output, ref sink);
            Assert.That(status.Code, Is.EqualTo(StatusCode.OK));
            return Item.Parser.ParseFrom(output.WrittenSpan.Slice(5));
        }

        private static async Task<Item> ProcessAsync(GrpcProcessor processor, GrpcMethod method, Query request)
        {
            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            await requestPipe.Writer.WriteAsync(Frame(request));
            requestPipe.Writer.Complete();
            await processor.ProcessAsync(new CallHead(processor.Resolve(method.PathUtf8.Span)), requestPipe.Reader, responsePipe.Writer, new NullSink());
            await responsePipe.Writer.FlushAsync();
            responsePipe.Writer.Complete();
            ReadResult result = await responsePipe.Reader.ReadAsync();
            while (!result.IsCompleted)
            {
                responsePipe.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                result = await responsePipe.Reader.ReadAsync();
            }

            return Item.Parser.ParseFrom(result.Buffer.Slice(5));
        }

        private static byte[] Frame(IMessage message)
        {
            byte[] payload = message.ToByteArray();
            var frame = new byte[5 + payload.Length];
            MessageFraming.WritePrefix(frame, payload.Length);
            payload.CopyTo(frame, 5);
            return frame;
        }
    }
}
