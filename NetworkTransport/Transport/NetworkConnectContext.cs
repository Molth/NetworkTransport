// ReSharper disable ALL

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Network
{
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public unsafe struct NetworkConnectContext
    {
        [FieldOffset(0)] public fixed byte data[32];

        public static implicit operator Span<byte>(in NetworkConnectContext connectContext) =>
            MemoryMarshal.CreateSpan(ref Unsafe.As<NetworkConnectContext, byte>(ref Unsafe.AsRef(in connectContext)), 32);
    }
}