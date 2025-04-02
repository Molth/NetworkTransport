using NativeCollections;

// ReSharper disable ALL

namespace Network
{
    public struct NetworkPacket
    {
        public required NetworkPacketFlag flag;
        public required NativeArray<byte> data;
    }
}