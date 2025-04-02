// ReSharper disable ALL

namespace Network
{
    public struct NetworkHostCreateOptions
    {
        public required uint version;
        public required ushort peerCount;
        public required ushort port;

        public uint socketSendBufferSize;
        public uint socketReceiveBufferSize;

        public uint duplicatePeers;

        public uint maximumSocketReceiveSize;
        public uint maximumReliableReceiveSize;

        public uint eventQueueSize;
        public uint eventQueueMaxFreeChunks;

        public NetworkConnectHook connectHook;
    }
}