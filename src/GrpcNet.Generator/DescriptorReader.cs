using System.Collections.Generic;
using System.Text;

namespace GrpcNet.Generator
{
    /// <summary>The parts of a <c>FileDescriptorProto</c> the generator needs.</summary>
    internal sealed class ProtoFile
    {
        public string Name { get; set; } = "";

        public string Package { get; set; } = "";

        public string? CSharpNamespace { get; set; }

        public List<string> Dependencies { get; } = new List<string>();

        public List<ProtoMessage> Messages { get; } = new List<ProtoMessage>();

        public List<ProtoService> Services { get; } = new List<ProtoService>();
    }

    internal sealed class ProtoMessage
    {
        public string Name { get; set; } = "";

        public bool MapEntry { get; set; }

        public List<ProtoMessage> Nested { get; } = new List<ProtoMessage>();
    }

    internal sealed class ProtoService
    {
        public string Name { get; set; } = "";

        public List<ProtoMethod> Methods { get; } = new List<ProtoMethod>();
    }

    internal sealed class ProtoMethod
    {
        public string Name { get; set; } = "";

        public string InputType { get; set; } = "";

        public string OutputType { get; set; } = "";

        public bool ClientStreaming { get; set; }

        public bool ServerStreaming { get; set; }
    }

    /// <summary>
    /// Reads a serialized <c>FileDescriptorProto</c>, keeping only the fields the generator uses and skipping the rest. Malformed
    /// input makes <see cref="TryRead"/> return false; it never throws.
    /// </summary>
    internal static class DescriptorReader
    {
        private const int MaxDepth = 64;

        private const int Varint = 0;
        private const int Fixed64 = 1;
        private const int LengthDelimited = 2;
        private const int StartGroup = 3;
        private const int EndGroup = 4;
        private const int Fixed32 = 5;

        public static bool TryRead(byte[] data, out ProtoFile file)
        {
            file = new ProtoFile();
            var reader = new Reader(data, 0, data.Length);
            return ReadFile(ref reader, file);
        }

        private static bool ReadFile(ref Reader reader, ProtoFile file)
        {
            while (!reader.AtEnd)
            {
                if (!reader.TryTag(out int field, out int wireType))
                {
                    return false;
                }

                switch (field)
                {
                    case 1:
                        if (!reader.TryString(wireType, out string name)) return false;
                        file.Name = name;
                        break;
                    case 2:
                        if (!reader.TryString(wireType, out string package)) return false;
                        file.Package = package;
                        break;
                    case 3:
                        if (!reader.TryString(wireType, out string dependency)) return false;
                        file.Dependencies.Add(dependency);
                        break;
                    case 4:
                    {
                        if (!reader.TryNested(wireType, out Reader nested)) return false;
                        var message = new ProtoMessage();
                        if (!ReadMessage(ref nested, message, 1)) return false;
                        file.Messages.Add(message);
                        break;
                    }
                    case 6:
                    {
                        if (!reader.TryNested(wireType, out Reader nested)) return false;
                        var service = new ProtoService();
                        if (!ReadService(ref nested, service)) return false;
                        file.Services.Add(service);
                        break;
                    }
                    case 8:
                    {
                        if (!reader.TryNested(wireType, out Reader nested)) return false;
                        if (!ReadFileOptions(ref nested, file)) return false;
                        break;
                    }
                    default:
                        if (!reader.TrySkip(field, wireType, 0)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool ReadFileOptions(ref Reader reader, ProtoFile file)
        {
            while (!reader.AtEnd)
            {
                if (!reader.TryTag(out int field, out int wireType)) return false;
                if (field == 37)
                {
                    if (!reader.TryString(wireType, out string ns)) return false;
                    file.CSharpNamespace = ns;
                }
                else if (!reader.TrySkip(field, wireType, 0))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ReadMessage(ref Reader reader, ProtoMessage message, int depth)
        {
            if (depth > MaxDepth)
            {
                return false;
            }

            while (!reader.AtEnd)
            {
                if (!reader.TryTag(out int field, out int wireType)) return false;
                switch (field)
                {
                    case 1:
                        if (!reader.TryString(wireType, out string name)) return false;
                        message.Name = name;
                        break;
                    case 3:
                    {
                        if (!reader.TryNested(wireType, out Reader nested)) return false;
                        var inner = new ProtoMessage();
                        if (!ReadMessage(ref nested, inner, depth + 1)) return false;
                        message.Nested.Add(inner);
                        break;
                    }
                    case 7:
                    {
                        if (!reader.TryNested(wireType, out Reader nested)) return false;
                        while (!nested.AtEnd)
                        {
                            if (!nested.TryTag(out int optionField, out int optionWireType)) return false;
                            if (optionField == 7)
                            {
                                if (!nested.TryBool(optionWireType, out bool mapEntry)) return false;
                                message.MapEntry = mapEntry;
                            }
                            else if (!nested.TrySkip(optionField, optionWireType, 0))
                            {
                                return false;
                            }
                        }

                        break;
                    }
                    default:
                        if (!reader.TrySkip(field, wireType, 0)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool ReadService(ref Reader reader, ProtoService service)
        {
            while (!reader.AtEnd)
            {
                if (!reader.TryTag(out int field, out int wireType)) return false;
                switch (field)
                {
                    case 1:
                        if (!reader.TryString(wireType, out string name)) return false;
                        service.Name = name;
                        break;
                    case 2:
                    {
                        if (!reader.TryNested(wireType, out Reader nested)) return false;
                        var method = new ProtoMethod();
                        if (!ReadMethod(ref nested, method)) return false;
                        service.Methods.Add(method);
                        break;
                    }
                    default:
                        if (!reader.TrySkip(field, wireType, 0)) return false;
                        break;
                }
            }

            return true;
        }

        private static bool ReadMethod(ref Reader reader, ProtoMethod method)
        {
            while (!reader.AtEnd)
            {
                if (!reader.TryTag(out int field, out int wireType)) return false;
                switch (field)
                {
                    case 1:
                        if (!reader.TryString(wireType, out string name)) return false;
                        method.Name = name;
                        break;
                    case 2:
                        if (!reader.TryString(wireType, out string input)) return false;
                        method.InputType = input;
                        break;
                    case 3:
                        if (!reader.TryString(wireType, out string output)) return false;
                        method.OutputType = output;
                        break;
                    case 5:
                        if (!reader.TryBool(wireType, out bool clientStreaming)) return false;
                        method.ClientStreaming = clientStreaming;
                        break;
                    case 6:
                        if (!reader.TryBool(wireType, out bool serverStreaming)) return false;
                        method.ServerStreaming = serverStreaming;
                        break;
                    default:
                        if (!reader.TrySkip(field, wireType, 0)) return false;
                        break;
                }
            }

            return true;
        }

        private struct Reader
        {
            private readonly byte[] _data;
            private readonly int _end;
            private int _position;

            public Reader(byte[] data, int start, int end)
            {
                _data = data;
                _position = start;
                _end = end;
            }

            public bool AtEnd => _position >= _end;

            public bool TryTag(out int field, out int wireType)
            {
                field = 0;
                wireType = 0;
                if (!TryVarint(out ulong tag) || tag > uint.MaxValue)
                {
                    return false;
                }

                field = (int)(tag >> 3);
                wireType = (int)(tag & 7);
                return field > 0;
            }

            public bool TryVarint(out ulong value)
            {
                value = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    if (_position >= _end)
                    {
                        return false;
                    }

                    byte b = _data[_position++];
                    if (shift == 63 && b > 1)
                    {
                        // The tenth byte may carry only the top bit of a 64-bit value.
                        return false;
                    }

                    value |= (ulong)(b & 0x7F) << shift;
                    if (b < 0x80)
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool TryBool(int wireType, out bool value)
            {
                value = false;
                if (wireType != Varint || !TryVarint(out ulong raw))
                {
                    return false;
                }

                value = raw != 0;
                return true;
            }

            public bool TryString(int wireType, out string value)
            {
                value = "";
                if (!TryLength(wireType, out int length))
                {
                    return false;
                }

                value = Encoding.UTF8.GetString(_data, _position, length);
                _position += length;
                return true;
            }

            public bool TryNested(int wireType, out Reader nested)
            {
                nested = default;
                if (!TryLength(wireType, out int length))
                {
                    return false;
                }

                nested = new Reader(_data, _position, _position + length);
                _position += length;
                return true;
            }

            public bool TrySkip(int field, int wireType, int depth)
            {
                switch (wireType)
                {
                    case Varint:
                        return TryVarint(out _);
                    case Fixed64:
                        return TryAdvance(8);
                    case LengthDelimited:
                        if (!TryLength(wireType, out int length)) return false;
                        _position += length;
                        return true;
                    case StartGroup:
                        if (depth >= MaxDepth) return false;
                        while (true)
                        {
                            if (!TryTag(out int inner, out int innerWireType)) return false;
                            if (innerWireType == EndGroup)
                            {
                                return inner == field;
                            }

                            if (!TrySkip(inner, innerWireType, depth + 1)) return false;
                        }

                    case Fixed32:
                        return TryAdvance(4);
                    default:
                        // An unmatched end-group, or wire types 6 and 7, which do not exist.
                        return false;
                }
            }

            private bool TryLength(int wireType, out int length)
            {
                length = 0;
                if (wireType != LengthDelimited || !TryVarint(out ulong raw) || raw > (ulong)(_end - _position))
                {
                    return false;
                }

                length = (int)raw;
                return true;
            }

            private bool TryAdvance(int count)
            {
                if (_end - _position < count)
                {
                    return false;
                }

                _position += count;
                return true;
            }
        }
    }
}
