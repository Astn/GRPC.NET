using System;
using System.Buffers;

namespace GrpcNet
{
    /// <summary>
    /// Holds a split message's bytes while it is decoded: per-thread scratch when it is free and large enough, otherwise a
    /// pooled buffer. Callers acquire, copy, decode and release with no await in between, so per-thread use is safe.
    /// </summary>
    internal static class PayloadScratch
    {
        // Provisional: the per-thread scratch keeps at most this much between calls (JSON-RPC.NET's cap). CALL-INTERFACE.md
        // lists scratch retention as still open.
        internal const int RetainedLimit = 64 * 1024;

        [ThreadStatic]
        private static byte[]? t_scratch;

        [ThreadStatic]
        private static bool t_inUse;

        /// <summary>Returns a buffer of at least <paramref name="size"/> bytes. <paramref name="rented"/> says how to release it.</summary>
        internal static byte[] Acquire(int size, out bool rented)
        {
            if (!t_inUse && size <= RetainedLimit)
            {
                t_inUse = true;
                rented = false;
                return t_scratch ??= new byte[RetainedLimit];
            }

            rented = true;
            return ArrayPool<byte>.Shared.Rent(size);
        }

        internal static void Release(byte[] buffer, bool rented)
        {
            if (rented)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            else
            {
                t_inUse = false;
            }
        }
    }
}
