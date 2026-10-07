using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace System.Runtime.CompilerServices
{
    // Records need this type, which netstandard2.0 lacks.
    internal static class IsExternalInit
    {
    }
}

namespace GrpcNet.Generator
{
    /// <summary>An immutable array compared by its elements, so pipeline values stay cacheable.</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly T[]? _items;

        public EquatableArray(T[] items) => _items = items;

        public int Count => _items?.Length ?? 0;

        public T this[int index] => _items![index];

        public bool Equals(EquatableArray<T> other)
        {
            T[] left = _items ?? Array.Empty<T>();
            T[] right = other._items ?? Array.Empty<T>();
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            int hash = 17;
            foreach (T item in _items ?? Array.Empty<T>())
            {
                hash = unchecked((hash * 31) + item.GetHashCode());
            }

            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal enum CallKind
    {
        Unary,
        ServerStreaming,
        ClientStreaming,
        Duplex,
    }

    /// <summary>One method, with its message types as fully qualified C# names.</summary>
    internal sealed record MethodModel(string Name, CallKind Kind, string RequestType, string ResponseType);

    internal sealed record ServiceModel(string Name, string FullName, EquatableArray<MethodModel> Methods);

    /// <summary>A source location kept as plain values, so the model holds no syntax tree.</summary>
    internal sealed record LocationInfo(string Path, TextSpan Span, LinePositionSpan Lines)
    {
        public Location ToLocation() => Location.Create(Path, Span, Lines);

        public static LocationInfo? From(Location? location)
        {
            if (location == null || location.SourceTree == null)
            {
                return null;
            }

            return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
        }
    }

    /// <summary>The C# class a service is generated into, and where the service comes from.</summary>
    internal sealed record ServiceTarget(string ClassName, string ProtoFile, string ServiceFullName);

    internal sealed record DiagnosticInfo(string Id, EquatableArray<string> Arguments, LocationInfo? Location);

    /// <summary>
    /// Everything the emitter needs for one protoc-generated file: the namespace and reflection class protoc emitted, and its
    /// services. A file with an error diagnostic emits nothing.
    /// </summary>
    internal sealed record FileModel(
        string ProtoFile,
        string Namespace,
        string ReflectionClass,
        LocationInfo? Location,
        EquatableArray<ServiceModel> Services,
        EquatableArray<DiagnosticInfo> Diagnostics);
}
