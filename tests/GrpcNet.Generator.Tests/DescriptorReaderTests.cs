using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;

namespace GrpcNet.Generator.Tests
{
    public class DescriptorReaderTests
    {
        private static IEnumerable<TestCaseData> Files()
        {
            yield return new TestCaseData(GrpcNet.Generator.Tests.Calc.CalcReflection.Descriptor).SetName("calc.proto");
            yield return new TestCaseData(Common.V1.CommonReflection.Descriptor).SetName("common.proto");
            yield return new TestCaseData(GrpcNet.Generator.Tests.Collide.CollideReflection.Descriptor).SetName("collide.proto");
            yield return new TestCaseData(GrpcNet.Generator.Tests.Quirks.QuirksReflection.Descriptor).SetName("quirks.proto");
            yield return new TestCaseData(NopkgReflection.Descriptor).SetName("nopkg.proto");
            yield return new TestCaseData(DescriptorReflection.Descriptor).SetName("descriptor.proto");
            yield return new TestCaseData(StructReflection.Descriptor).SetName("struct.proto");
            yield return new TestCaseData(ApiReflection.Descriptor).SetName("api.proto");
            yield return new TestCaseData(TypeReflection.Descriptor).SetName("type.proto");
            yield return new TestCaseData(EmptyReflection.Descriptor).SetName("empty.proto");
        }

        [TestCaseSource(nameof(Files))]
        public void ReadsWhatGoogleProtobufReads(FileDescriptor descriptor)
        {
            byte[] data = descriptor.SerializedData.ToByteArray();
            Assert.That(DescriptorReader.TryRead(data, out ProtoFile file), Is.True);
            AssertSame(FileDescriptorProto.Parser.ParseFrom(data), file);
        }

        [Test]
        public void SkipsUnknownFieldsOfEveryWireType()
        {
            byte[] clean = GrpcNet.Generator.Tests.Calc.CalcReflection.Descriptor.SerializedData.ToByteArray();
            FileDescriptorProto proto = FileDescriptorProto.Parser.ParseFrom(clean);

            // Unknown fields at the method, service, nested message, message and file levels, innermost first; Google.Protobuf
            // keeps unknown fields through each re-parse.
            int outerIndex = proto.MessageType.IndexOf(proto.MessageType.Single(m => m.Name == "Outer"));
            DescriptorProto outer = proto.MessageType[outerIndex];
            outer.NestedType[0] = DescriptorProto.Parser.ParseFrom(WithUnknownFields(outer.NestedType[0].ToByteArray()));
            proto.MessageType[outerIndex] = DescriptorProto.Parser.ParseFrom(WithUnknownFields(outer.ToByteArray()));
            ServiceDescriptorProto service = proto.Service[0];
            service.Method[2] = MethodDescriptorProto.Parser.ParseFrom(WithUnknownFields(service.Method[2].ToByteArray()));
            proto.Service[0] = ServiceDescriptorProto.Parser.ParseFrom(WithUnknownFields(service.ToByteArray()));
            byte[] data = WithUnknownFields(proto.ToByteArray());
            Assert.That(data.Length, Is.GreaterThan(clean.Length + 5 * 40), "the unknown fields survived the round trip");

            Assert.That(DescriptorReader.TryRead(data, out ProtoFile file), Is.True);
            AssertSame(FileDescriptorProto.Parser.ParseFrom(clean), file);
        }

        [Test]
        public void NeverThrowsOnAnyTruncation()
        {
            byte[] data = DescriptorReflection.Descriptor.SerializedData.ToByteArray();
            int rejected = 0;
            for (int length = 0; length < data.Length; length++)
            {
                if (!DescriptorReader.TryRead(data.AsSpan(0, length).ToArray(), out _))
                {
                    rejected++;
                }
            }

            // Most cuts fall inside a field; a cut on a field boundary is a valid, shorter descriptor.
            Assert.That(rejected, Is.GreaterThan(data.Length / 2));
        }

        private static IEnumerable<TestCaseData> Malformed()
        {
            yield return new TestCaseData(new byte[] { 0x0A, 0x05, 0x61 }).SetName("length beyond the end");
            yield return new TestCaseData(new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }).SetName("length above int.MaxValue");
            yield return new TestCaseData(new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }).SetName("length of 2^64-1");
            yield return new TestCaseData(new byte[] { 0x78, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }).SetName("unknown varint of eleven bytes");
            yield return new TestCaseData(new byte[] { 0x78, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02 }).SetName("unknown varint overflowing 64 bits");
            yield return new TestCaseData(new byte[] { 0x78, 0x80 }).SetName("unknown varint cut short");
            yield return new TestCaseData(new byte[] { 0x08, 0x01 }).SetName("known string field sent as a varint");
            yield return new TestCaseData(new byte[] { 0x32, 0x04, 0x12, 0x02, 0x2A, 0x00 }).SetName("method streaming flag sent as a string");
            yield return new TestCaseData(new byte[] { 0x7C }).SetName("end group without a start");
            yield return new TestCaseData(new byte[] { 0x7B, 0x84, 0x01 }).SetName("group ended by another field's end group");
            yield return new TestCaseData(new byte[] { 0x7B, 0x08, 0x01 }).SetName("group never ended");
            yield return new TestCaseData(new byte[] { 0x7E, 0x00 }).SetName("wire type 6");
            yield return new TestCaseData(new byte[] { 0x7F, 0x00 }).SetName("wire type 7");
            yield return new TestCaseData(new byte[] { 0x02, 0x00 }).SetName("field number zero");
            yield return new TestCaseData(new byte[] { 0x79, 0x00, 0x00, 0x00 }).SetName("fixed64 cut short");
            yield return new TestCaseData(new byte[] { 0x7D, 0x00, 0x00 }).SetName("fixed32 cut short");
            yield return new TestCaseData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }).SetName("tag above uint.MaxValue");
            yield return new TestCaseData(NestedMessages(100)).SetName("messages nested 100 deep");
            yield return new TestCaseData(NestedGroups(100)).SetName("groups nested 100 deep");
        }

        [TestCaseSource(nameof(Malformed))]
        public void RejectsMalformedInput(byte[] data)
        {
            Assert.That(DescriptorReader.TryRead(data, out _), Is.False);
        }

        [Test]
        public void AcceptsNestingWithinTheLimit()
        {
            Assert.That(DescriptorReader.TryRead(NestedMessages(60), out ProtoFile file), Is.True);
            Assert.That(file.Messages, Has.Count.EqualTo(1));
            Assert.That(DescriptorReader.TryRead(NestedGroups(60), out _), Is.True);
        }

        // Appends one unknown field of each wire type, including a group holding a nested unknown field.
        private static byte[] WithUnknownFields(byte[] message)
        {
            var stream = new MemoryStream();
            stream.Write(message, 0, message.Length);
            var output = new CodedOutputStream(stream);
            output.WriteTag(900, WireFormat.WireType.Varint);
            output.WriteUInt64(ulong.MaxValue);
            output.WriteTag(901, WireFormat.WireType.Fixed64);
            output.WriteFixed64(42);
            output.WriteTag(902, WireFormat.WireType.LengthDelimited);
            output.WriteString("unknown");
            output.WriteTag(903, WireFormat.WireType.StartGroup);
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt32(7);
            output.WriteTag(903, WireFormat.WireType.EndGroup);
            output.WriteTag(904, WireFormat.WireType.Fixed32);
            output.WriteFixed32(42);
            output.Flush();
            return stream.ToArray();
        }

        private static byte[] NestedMessages(int depth)
        {
            var message = new DescriptorProto { Name = "Leaf" };
            for (int i = 0; i < depth - 1; i++)
            {
                var outer = new DescriptorProto { Name = "Level" + i };
                outer.NestedType.Add(message);
                message = outer;
            }

            var file = new FileDescriptorProto { Name = "deep.proto" };
            file.MessageType.Add(message);
            return file.ToByteArray();
        }

        private static byte[] NestedGroups(int depth)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < depth; i++)
            {
                bytes.AddRange(new byte[] { 0x7B }); // field 15, start group
            }

            for (int i = 0; i < depth; i++)
            {
                bytes.AddRange(new byte[] { 0x7C }); // field 15, end group
            }

            return bytes.ToArray();
        }

        private static void AssertSame(FileDescriptorProto expected, ProtoFile actual)
        {
            Assert.That(actual.Name, Is.EqualTo(expected.Name));
            Assert.That(actual.Package, Is.EqualTo(expected.Package));
            Assert.That(actual.CSharpNamespace, Is.EqualTo(expected.Options?.HasCsharpNamespace == true ? expected.Options.CsharpNamespace : null));
            Assert.That(actual.Dependencies, Is.EqualTo(expected.Dependency.ToList()));
            AssertSame(expected.MessageType.ToList(), actual.Messages, expected.Name);
            Assert.That(actual.Services.Select(s => s.Name), Is.EqualTo(expected.Service.Select(s => s.Name)));
            for (int i = 0; i < expected.Service.Count; i++)
            {
                MethodDescriptorProto[] methods = expected.Service[i].Method.ToArray();
                ProtoMethod[] read = actual.Services[i].Methods.ToArray();
                Assert.That(read.Select(m => (m.Name, m.InputType, m.OutputType, m.ClientStreaming, m.ServerStreaming)),
                    Is.EqualTo(methods.Select(m => (m.Name, m.InputType, m.OutputType, m.ClientStreaming, m.ServerStreaming))));
            }
        }

        private static void AssertSame(List<DescriptorProto> expected, List<ProtoMessage> actual, string path)
        {
            Assert.That(actual.Select(m => (m.Name, m.MapEntry)), Is.EqualTo(expected.Select(m => (m.Name, m.Options?.MapEntry ?? false))), path);
            for (int i = 0; i < expected.Count; i++)
            {
                AssertSame(expected[i].NestedType.ToList(), actual[i].Nested, path + "/" + expected[i].Name);
            }
        }
    }
}
