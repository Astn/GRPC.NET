using System;
using System.Collections.Generic;
using System.Text;

namespace GrpcNet
{
    /// <summary>
    /// The fixed table of methods, built once. <see cref="Resolve"/> looks a path up by its UTF-8 bytes in an open-addressing
    /// array: one hash of the bytes, then a byte comparison. No string and no allocation.
    /// </summary>
    internal sealed class MethodTable
    {
        internal delegate int PathHasher(ReadOnlySpan<byte> path);

        private readonly MethodEntry[] _entries;
        private readonly int[] _slots;          // entry index + 1; 0 marks an empty slot
        private readonly int[] _slotHashes;
        private readonly int _mask;
        private readonly PathHasher _hasher;

        internal MethodTable(IReadOnlyList<MethodEntry> entries, PathHasher? hasher = null)
        {
            _hasher = hasher ?? DefaultHash;
            _entries = new MethodEntry[entries.Count];
            int capacity = 4;
            while (capacity < entries.Count * 2)
            {
                capacity <<= 1;
            }

            _slots = new int[capacity];
            _slotHashes = new int[capacity];
            _mask = capacity - 1;

            for (int i = 0; i < entries.Count; i++)
            {
                MethodEntry entry = entries[i];
                if (Find(entry.PathUtf8, _hasher(entry.PathUtf8)) >= 0)
                {
                    throw new InvalidOperationException("The method '" + entry.Path + "' is bound more than once.");
                }

                _entries[i] = entry;
                entry.Id = new MethodId(i);
                int hash = _hasher(entry.PathUtf8);
                int slot = hash & _mask;
                while (_slots[slot] != 0)
                {
                    slot = (slot + 1) & _mask;
                }

                _slots[slot] = i + 1;
                _slotHashes[slot] = hash;
            }
        }

        internal int Count => _entries.Length;

        internal MethodId Resolve(ReadOnlySpan<byte> pathUtf8)
        {
            int index = Find(pathUtf8, _hasher(pathUtf8));
            return index < 0 ? MethodId.None : new MethodId(index);
        }

        internal MethodEntry? Get(MethodId id)
        {
            int index = id.Index;
            return (uint)index < (uint)_entries.Length ? _entries[index] : null;
        }

        private int Find(ReadOnlySpan<byte> pathUtf8, int hash)
        {
            int slot = hash & _mask;
            while (true)
            {
                int indexPlusOne = _slots[slot];
                if (indexPlusOne == 0)
                {
                    return -1;
                }

                MethodEntry? entry = _entries[indexPlusOne - 1];
                if (_slotHashes[slot] == hash && entry != null && pathUtf8.SequenceEqual(entry.PathUtf8))
                {
                    return indexPlusOne - 1;
                }

                slot = (slot + 1) & _mask;
            }
        }

        private static int DefaultHash(ReadOnlySpan<byte> path)
        {
            var hash = new HashCode();
            hash.AddBytes(path);
            return hash.ToHashCode();
        }

        internal static byte[] EncodePath(string path)
        {
            return Encoding.UTF8.GetBytes(path);
        }
    }
}
