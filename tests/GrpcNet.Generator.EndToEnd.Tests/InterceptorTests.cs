using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Empty = Google.Protobuf.WellKnownTypes.Empty;
using GrpcNet.Generator.EndToEnd.Shop;
using GrpcNet.Protobuf;
using NUnit.Framework;

namespace GrpcNet.Generator.EndToEnd.Tests
{
    public sealed class ShopService
    {
        private readonly string _prefix;

        public ShopService(string prefix) => _prefix = prefix;

        public Item Get(Query query) => new Item { Name = _prefix + "get" + query.Count };

        public async ValueTask<Item> Find(Query query, CancellationToken cancellation)
        {
            await Task.Yield();
            return new Item { Name = _prefix + "find" + query.Count };
        }

        public async ValueTask List(Query query, MessageWriter<Item> responses, CancellationToken cancellation)
        {
            for (int i = 0; i < query.Count; i++)
            {
                await responses.WriteAsync(new Item { Name = _prefix + i });
            }
        }

        public async ValueTask<Item.Types.Price> Total(MessageReader<Item.Types.Price> prices, CancellationToken cancellation)
        {
            int total = 0;
            while (await prices.MoveNextAsync())
            {
                total += prices.Current.Cents;
            }

            return new Item.Types.Price { Cents = total };
        }

        public async ValueTask Echo(MessageReader<Item> items, MessageWriter<Item> responses, CancellationToken cancellation)
        {
            while (await items.MoveNextAsync())
            {
                await responses.WriteAsync(new Item { Name = _prefix + items.Current.Name });
            }
        }

        public ValueTask<Empty> Ping(Empty empty) => new ValueTask<Empty>(empty);

        public Item Overloaded(Query query) => new Item();

        public Item Overloaded(Query query, int extra) => new Item();
    }

    public static class Notes
    {
        public static GrpcNet.Generator.EndToEnd.Merge.Note Shout(GrpcNet.Generator.EndToEnd.Merge.Note note) => new GrpcNet.Generator.EndToEnd.Merge.Note { Text = note.Text + "!" };
    }

    public static class StaticShop
    {
        public static Item Get(Query query) => new Item { Name = "static" + query.Count };
    }

    public class BaseShop
    {
        public virtual Item Get(Query query) => new Item { Name = "base" };
    }

    public sealed class DerivedShop : BaseShop
    {
        public override Item Get(Query query) => new Item { Name = "derived" };
    }

    public sealed class GenericHost<T>
        where T : class
    {
        public Item Get(Query query) => new Item { Name = "host" };

        // The receiver's type mentions T, which generated code cannot name: the delegate stays.
        public GrpcProcessorBuilder BindOwn(GrpcProcessorBuilder builder) => builder.Bind(Shop.Shop.Get, Get);

        // Everything named is closed, so the call is intercepted even inside a generic type.
        public GrpcProcessorBuilder BindStatic(GrpcProcessorBuilder builder) => builder.Bind(Shop.Shop.Get, StaticShop.Get);
    }

    /// <summary>Handlers bound as method groups run through generated struct invokers that call the method directly.</summary>
    public class InterceptorTests
    {
        private static Type InvokerOf(GrpcProcessor processor, GrpcMethod method)
            => processor.EntryOf(processor.Resolve(method.PathUtf8.Span))!.GetType().GetGenericArguments().Last();

        private static bool IsDirect(GrpcProcessor processor, GrpcMethod method)
        {
            Type invoker = InvokerOf(processor, method);
            return !invoker.IsGenericType && invoker.FullName!.Contains("GrpcNetInterceptors", StringComparison.Ordinal);
        }

        private static Item PrivateGet(Query query) => new Item();

        private static GrpcProcessorBuilder BindInGenericMethod<T>(GrpcProcessorBuilder builder) => builder.Bind(Shop.Shop.Get, StaticShop.Get);

        [Test]
        public async Task EveryCallKindBoundAsAMethodGroupIsCalledDirectly()
        {
            var service = new ShopService("s:");
            GrpcProcessor processor = new GrpcProcessorBuilder()
                .Bind(Shop.Shop.Get, service.Get)
                .BindAsync(Shop.Shop.Find, service.Find)
                .Bind(Shop.Shop.List, service.List)
                .Bind(Shop.Shop.Total, service.Total)
                .Bind(Shop.Shop.Echo, service.Echo)
                .BindAsync(Shop.Shop.Ping, service.Ping)
                .Build();

            foreach (GrpcMethod method in new GrpcMethod[] { Shop.Shop.Get, Shop.Shop.Find, Shop.Shop.List, Shop.Shop.Total, Shop.Shop.Echo, Shop.Shop.Ping })
            {
                Assert.That(IsDirect(processor, method), Is.True, method.Path + " has " + InvokerOf(processor, method));
            }

            Assert.That(processor.IsSynchronousUnary(processor.Resolve(Shop.Shop.Get.PathUtf8.Span)), Is.True);
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Get, Item.Parser, new Query { Count = 1 })).Single().Name, Is.EqualTo("s:get1"));
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Find, Item.Parser, new Query { Count = 2 })).Single().Name, Is.EqualTo("s:find2"));
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.List, Item.Parser, new Query { Count = 2 })).Select(i => i.Name), Is.EqualTo(new[] { "s:0", "s:1" }));
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Total, Item.Types.Price.Parser, new Item.Types.Price { Cents = 2 }, new Item.Types.Price { Cents = 3 })).Single().Cents, Is.EqualTo(5));
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Echo, Item.Parser, new Item { Name = "x" })).Single().Name, Is.EqualTo("s:x"));
            Assert.That(await BinderTests.CallAsync(processor, Shop.Shop.Ping, Empty.Parser, new Empty()), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task StaticMethodsAndTheStaticCallFormAreCalledDirectly()
        {
            var builder = new GrpcProcessorBuilder();
            ProtobufBindings.Bind(builder, Shop.Shop.Get, StaticShop.Get);
            ProtobufBindings.Bind(builder, Shop.Shop.Find, new ShopService("f:").Get);
            GrpcProcessor processor = builder.Build();
            Assert.That(IsDirect(processor, Shop.Shop.Get), Is.True);
            Assert.That(IsDirect(processor, Shop.Shop.Find), Is.True);
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Get, Item.Parser, new Query { Count = 4 })).Single().Name, Is.EqualTo("static4"));
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Find, Item.Parser, new Query { Count = 5 })).Single().Name, Is.EqualTo("f:get5"));
        }

        [Test]
        public async Task TheReceiverIsTheOneTheCallSiteEvaluated()
        {
            var service = new ShopService("first:");
            var builder = new GrpcProcessorBuilder().Bind(Shop.Shop.Get, service.Get);
            service = new ShopService("second:");
            GrpcProcessor processor = builder.Build();
            Assert.That(IsDirect(processor, Shop.Shop.Get), Is.True);
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Get, Item.Parser, new Query { Count = 1 })).Single().Name, Is.EqualTo("first:get1"));
            GC.KeepAlive(service);
        }

        [Test]
        public async Task AVirtualMethodOnADerivedReceiverRunsTheOverride()
        {
            BaseShop shop = new DerivedShop();
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(Shop.Shop.Get, shop.Get).Build();
            Assert.That(IsDirect(processor, Shop.Shop.Get), Is.True);
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Get, Item.Parser, new Query())).Single().Name, Is.EqualTo("derived"));
        }

        [Test]
        public async Task BinderFactoriesWithMethodGroupsAreCalledDirectly()
        {
            var service = new ShopService("b:");
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(new Shop.Shop.Binder
            {
                Get = Shop.Shop.Get.Sync(service.Get),
                Find = Shop.Shop.Find.Async(service.Find),
                List = service.List,
                Total = service.Total,
                Echo = service.Echo,
                Ping = Shop.Shop.Ping.Async(service.Ping),
            }).Build();

            Assert.That(IsDirect(processor, Shop.Shop.Get), Is.True);
            Assert.That(IsDirect(processor, Shop.Shop.Find), Is.True);
            Assert.That(IsDirect(processor, Shop.Shop.Ping), Is.True);
            Assert.That(IsDirect(processor, Shop.Shop.List), Is.False, "a binder's streaming member is assigned, not passed to a call");
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Get, Item.Parser, new Query { Count = 7 })).Single().Name, Is.EqualTo("b:get7"));
            Assert.That((await BinderTests.CallAsync(processor, Shop.Shop.Find, Item.Parser, new Query { Count = 8 })).Single().Name, Is.EqualTo("b:find8"));
        }

        [Test]
        public void ADirectHandlerFromAnotherMethodIsStillRefused()
        {
            var service = new ShopService("x");
            var binder = new Shop.Shop.Binder
            {
                Get = Shop.Shop.Get.Sync(service.Get),
                Find = Shop.Shop.Get.Sync(service.Get),
                List = service.List,
                Total = service.Total,
                Echo = service.Echo,
                Ping = Shop.Shop.Ping.Async(service.Ping),
            };
            var builder = new GrpcProcessorBuilder();
            Exception? ex = Assert.Catch(() => builder.Bind(binder));
            Assert.That(builder.Bindings, Is.Empty);
            Assert.That(ex!.Message, Does.Contain("handler for 'Find' made from '/shop.Shop/Get'"));
            Assert.That(Assert.Throws<ArgumentException>(() => GeneratedSupport.BindUnary(builder, Shop.Shop.Find, Shop.Shop.Get.Sync(service.Get)))!.Message,
                Does.Contain("was made from '/shop.Shop/Get'"));
            Assert.That(builder.Bindings, Is.Empty);
        }

        [Test]
        public void HandlersTheGeneratorCannotCallDirectlyKeepTheirDelegate()
        {
            var service = new ShopService("");
            Func<Query, Item> variable = service.Get;
            Assert.That(IsDirect(new GrpcProcessorBuilder().Bind(Shop.Shop.Get, q => service.Get(q)).Build(), Shop.Shop.Get), Is.False, "a lambda");
            Assert.That(IsDirect(new GrpcProcessorBuilder().Bind(Shop.Shop.Get, variable).Build(), Shop.Shop.Get), Is.False, "a delegate variable");
            Assert.That(IsDirect(new GrpcProcessorBuilder().Bind(Shop.Shop.Get, PrivateGet).Build(), Shop.Shop.Get), Is.False, "a private method");
            Assert.That(IsDirect(new GrpcProcessorBuilder().Bind(Shop.Shop.Get, service.Overloaded).Build(), Shop.Shop.Get), Is.False, "an overloaded method");
            Assert.That(IsDirect(new GenericHost<string>().BindOwn(new GrpcProcessorBuilder()).Build(), Shop.Shop.Get), Is.False, "a receiver whose type has a type parameter");
        }

        [Test]
        public void AMethodObjectInAClassGrpcAlsoGeneratesIsCalledDirectly()
        {
            // Merger exists before this generator runs (Grpc's plugin emits it), but its Ping member does not.
            GrpcProcessor processor = new GrpcProcessorBuilder().Bind(new GrpcNet.Generator.EndToEnd.Merge.Merger.Binder
            {
                Ping = GrpcNet.Generator.EndToEnd.Merge.Merger.Ping.Sync(Notes.Shout),
            }).Build();
            Assert.That(IsDirect(processor, GrpcNet.Generator.EndToEnd.Merge.Merger.Ping), Is.True);
        }

        [Test]
        public void ClosedCallsInsideGenericCodeAreCalledDirectly()
        {
            Assert.That(IsDirect(new GenericHost<string>().BindStatic(new GrpcProcessorBuilder()).Build(), Shop.Shop.Get), Is.True, "in a generic type");
            Assert.That(IsDirect(BindInGenericMethod<string>(new GrpcProcessorBuilder()).Build(), Shop.Shop.Get), Is.True, "in a generic method");
        }
    }
}
