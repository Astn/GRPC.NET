using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using GrpcNet.Generator.EndToEnd.Merge;
using GrpcNet.Generator.EndToEnd.Shop;
using GrpcNet.Protobuf;
using NUnit.Framework;

namespace GrpcNet.Generator.EndToEnd.Tests
{
    /// <summary>The generator running as an analyzer in a normal build, on protoc's output for this project's protos.</summary>
    public class BinderTests
    {
        private sealed class Status
        {
            public GrpcStatus Value;
            public int Completions;
        }

        private readonly struct Sink : ICallSink
        {
            private readonly Status _status;

            public Sink(Status status) => _status = status;

            public void WriteHeaders(in MetadataView headers)
            {
            }

            public void Complete(in GrpcStatus status, in MetadataView trailers)
            {
                _status.Value = status;
                _status.Completions++;
            }
        }

        private static Shop.Shop.Binder ShopBinder() => new Shop.Shop.Binder
        {
            Get = Shop.Shop.Get.Sync(q => new Item { Name = "item" + q.Count, Price = new Item.Types.Price { Cents = q.Count * 100 } }),
            Find = Shop.Shop.Find.Async(async (q, ct) =>
            {
                await Task.Delay(1, ct);
                return new Item { Name = "found" + q.Count };
            }),
            List = async (q, responses, ct) =>
            {
                for (int i = 0; i < q.Count; i++)
                {
                    await responses.WriteAsync(new Item { Name = "item" + i });
                }
            },
            Total = async (prices, ct) =>
            {
                int total = 0;
                while (await prices.MoveNextAsync())
                {
                    total += prices.Current.Cents;
                }

                return new Item.Types.Price { Cents = total };
            },
            Echo = async (items, responses, ct) =>
            {
                while (await items.MoveNextAsync())
                {
                    await responses.WriteAsync(new Item { Name = items.Current.Name + "!" });
                }
            },
            Ping = Shop.Shop.Ping.Async(async e =>
            {
                await Task.Yield();
                return e;
            }),
        };

        [Test]
        public async Task BinderBoundServiceRunsEveryCallKind()
        {
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(ShopBinder()).Build();

            Item item = (await CallAsync(processor, Shop.Shop.Get, Item.Parser, new Query { Count = 3 })).Single();
            Assert.That((item.Name, item.Price.Cents), Is.EqualTo(("item3", 300)));
            Assert.That(processor.IsSynchronousUnary(processor.Resolve(Shop.Shop.Get.PathUtf8.Span)), Is.True, "a Sync handler keeps the synchronous path");
            Assert.That((await CallAsync(processor, Shop.Shop.Find, Item.Parser, new Query { Count = 7 })).Single().Name, Is.EqualTo("found7"));

            List<Item> listed = await CallAsync(processor, Shop.Shop.List, Item.Parser, new Query { Count = 3 });
            Assert.That(listed.Select(i => i.Name), Is.EqualTo(new[] { "item0", "item1", "item2" }));

            Item.Types.Price total = (await CallAsync(processor, Shop.Shop.Total, Item.Types.Price.Parser,
                new Item.Types.Price { Cents = 1 }, new Item.Types.Price { Cents = 20 }, new Item.Types.Price { Cents = 300 })).Single();
            Assert.That(total.Cents, Is.EqualTo(321));

            List<Item> echoed = await CallAsync(processor, Shop.Shop.Echo, Item.Parser, new Item { Name = "a" }, new Item { Name = "b" });
            Assert.That(echoed.Select(i => i.Name), Is.EqualTo(new[] { "a!", "b!" }));

            Assert.That(await CallAsync(processor, Shop.Shop.Ping, Empty.Parser, new Empty()), Has.Count.EqualTo(1));
        }

        [Test]
        public void BinderWithAMissingHandlerBindsNothing()
        {
            Shop.Shop.Binder binder = ShopBinder();
            binder = new Shop.Shop.Binder { Get = binder.Get, Find = binder.Find, List = binder.List, Total = null!, Echo = binder.Echo, Ping = binder.Ping };
            var builder = new GrpcProcessorBuilder();
            ArgumentException? ex = Assert.Throws<ArgumentException>(() => builder.Bind(binder));
            Assert.That(ex!.Message, Does.Contain("no handler for 'Total'"));
            Assert.That(builder.Bindings, Is.Empty);
        }

        [Test]
        public void BinderWithAnEmptyUnaryHandlerBindsNothing()
        {
            Shop.Shop.Binder binder = ShopBinder();
            // Find is bound after Get, so a check made only when Find is bound would leave Get bound.
            binder = new Shop.Shop.Binder { Get = binder.Get, Find = default, List = binder.List, Total = binder.Total, Echo = binder.Echo, Ping = binder.Ping };
            var builder = new GrpcProcessorBuilder();
            Exception? ex = Assert.Catch(() => builder.Bind(binder));
            Assert.That(builder.Bindings, Is.Empty, "nothing is bound, not even Get");
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(ex!.Message, Does.Contain("no handler for 'Find'"));
        }

        [Test]
        public void BinderWithAHandlerMadeFromAnotherMethodBindsNothing()
        {
            // Get has Find's request and response types, so this compiles; the binder must still refuse it. Find is bound after
            // Get, so a check made only when Find is bound would leave Get bound.
            Shop.Shop.Binder binder = ShopBinder();
            binder = new Shop.Shop.Binder { Get = binder.Get, Find = Shop.Shop.Get.Sync(q => new Item()), List = binder.List, Total = binder.Total, Echo = binder.Echo, Ping = binder.Ping };
            var builder = new GrpcProcessorBuilder();
            Exception? ex = Assert.Catch(() => builder.Bind(binder));
            Assert.That(builder.Bindings, Is.Empty, "nothing is bound, not even Get");
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(ex!.Message, Does.Contain("handler for 'Find' made from '/shop.Shop/Get'"));
        }

        [Test]
        public void BindUnaryOutsideABinderChecksTheHandler()
        {
            var builder = new GrpcProcessorBuilder();
            Assert.That(Assert.Throws<ArgumentException>(() => GeneratedSupport.BindUnary(builder, Shop.Shop.Get, default))!.Message, Does.Contain("is empty"));
            Assert.That(Assert.Throws<ArgumentException>(() => GeneratedSupport.BindUnary(builder, Shop.Shop.Get, Shop.Shop.Find.Sync(q => new Item())))!.Message,
                Does.Contain("was made from '/shop.Shop/Find'"));
            Assert.That(builder.Bindings, Is.Empty);
        }

        [Test]
        public void HandlerFactoriesRejectNull()
        {
            Assert.Throws<ArgumentNullException>(() => Shop.Shop.Get.Sync(null!));
            Assert.Throws<ArgumentNullException>(() => Shop.Shop.Get.Async((Func<Query, ValueTask<Item>>)null!));
            Assert.Throws<ArgumentNullException>(() => Shop.Shop.Get.Async((Func<Query, System.Threading.CancellationToken, ValueTask<Item>>)null!));
        }

        [Test]
        public void BinderOverAnAlreadyBoundMethodBindsNothingMore()
        {
            var builder = new GrpcProcessorBuilder().Bind(Shop.Shop.Get, q => new Item());
            InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => builder.Bind(ShopBinder()));
            Assert.That(ex!.Message, Does.Contain("/shop.Shop/Get is already bound"));
            Assert.That(builder.Bindings.Select(b => b.Path), Is.EqualTo(new[] { "/shop.Shop/Get" }));
        }

        [Test]
        public void GeneratedClassMergesWithGrpcsOwnServiceClass()
        {
            // Grpc's plugin generated BindService and MergerBase into the same static partial class.
            Assert.That(typeof(Merger).GetMethod("BindService", new[] { typeof(Merger.MergerBase) }), Is.Not.Null);
            Assert.That(Merger.Ping, Is.InstanceOf<UnaryMethod<Note, Note>>());

            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(new Merger.Binder { Ping = Merger.Ping.Sync(n => new Note { Text = n.Text + "!" }) }).Build();
            Assert.That(processor.Resolve("/merge.Merger/Ping"u8).IsValid, Is.True);
        }

        internal static async Task<List<TResponse>> CallAsync<TResponse>(GrpcProcessor processor, GrpcMethod method, MessageParser<TResponse> parser, params IMessage[] requests)
            where TResponse : IMessage<TResponse>
        {
            var requestPipe = new Pipe();
            var responsePipe = new Pipe();
            foreach (IMessage request in requests)
            {
                byte[] payload = request.ToByteArray();
                var frame = new byte[5 + payload.Length];
                MessageFraming.WritePrefix(frame, payload.Length);
                payload.CopyTo(frame, 5);
                await requestPipe.Writer.WriteAsync(frame);
            }

            requestPipe.Writer.Complete();
            var status = new Status();
            await processor.ProcessAsync(new CallHead(processor.Resolve(method.PathUtf8.Span)), requestPipe.Reader, responsePipe.Writer, new Sink(status));
            Assert.That((status.Value.Code, status.Completions), Is.EqualTo((StatusCode.OK, 1)), method.Path);
            await responsePipe.Writer.FlushAsync();
            responsePipe.Writer.Complete();

            ReadResult result = await responsePipe.Reader.ReadAsync();
            while (!result.IsCompleted)
            {
                responsePipe.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                result = await responsePipe.Reader.ReadAsync();
            }

            byte[] bytes = result.Buffer.ToArray();
            var responses = new List<TResponse>();
            for (int offset = 0; offset < bytes.Length;)
            {
                MessageFraming.TryReadPrefix(bytes.AsSpan(offset), out _, out uint length);
                responses.Add(parser.ParseFrom(bytes.AsSpan(offset + 5, (int)length)));
                offset += 5 + (int)length;
            }

            return responses;
        }
    }
}
