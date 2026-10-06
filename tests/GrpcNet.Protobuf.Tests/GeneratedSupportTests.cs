using System;
using GrpcNet.Protobuf.Tests.Messages;
using NUnit.Framework;

namespace GrpcNet.Protobuf.Tests
{
    /// <summary>What the generator emits for a service whose .proto has since lost the method: the class fails to initialize.</summary>
    internal static class DriftedService
    {
        public static readonly UnaryMethod<Scalar, Scalar> Gone = new(TestReflection.Descriptor, "test.Calculator", "Gone");
    }

    /// <summary>The first two statements of every generated binder, over the drifted class.</summary>
    internal sealed class DriftedBinder : IServiceBinder
    {
        public void BindTo(GrpcProcessorBuilder builder)
        {
            GeneratedSupport.EnsureInitialized(typeof(DriftedService));
            GeneratedSupport.PreflightService(builder, GeneratedSupport.FindService(TestReflection.Descriptor, "test.Calculator"),
                new GrpcMethod[] { DriftedService.Gone }, new object?[] { null }, new[] { "Gone" });
        }
    }

    public class GeneratedSupportTests
    {
        [Test]
        public void DriftedServiceFailsAtBinderRegistrationWithItsOwnException()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var builder = new GrpcProcessorBuilder();
                InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => builder.Bind(new DriftedBinder()));
                Assert.That(ex!.Message, Does.Contain("'test.Calculator.Gone' is not declared").And.Contain("out of date"));
                Assert.That(builder.Bindings, Is.Empty);
            }

            // Without the helper the same failure reaches the caller wrapped.
            TypeInitializationException? wrapped = Assert.Throws<TypeInitializationException>(() => _ = DriftedService.Gone);
            Assert.That(wrapped!.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void FindServiceRejectsAServiceTheFileDoesNotDeclare()
        {
            Assert.That(GeneratedSupport.FindService(TestReflection.Descriptor, "test.Calculator").Name, Is.EqualTo("Calculator"));
            InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => GeneratedSupport.FindService(TestReflection.Descriptor, "test.Missing"));
            Assert.That(ex!.Message, Does.Contain("'test.Missing' is not declared in 'test.proto'"));
        }
    }
}
