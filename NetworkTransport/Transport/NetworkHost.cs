using NanoSockets;
using NativeCollections;

// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkHost
    {
        public uint version;

        public uint peerCount;

        public uint serviceTimestamp;

        public UnsafeSparseSet<nint> peerIDs;
        public UnsafeQueue<ushort> freeIDs;
        public NetworkPeer* peers;

        public Socket socket;

        public uint duplicatePeers;

        public uint maximumSocketReceiveSize;
        public uint maximumReliableReceiveSize;

        public UnsafeChunkedQueue<NetworkEvent> incomingEvents;

        public uint connectedPeers;

        public NetworkConnectHook connectHook;

        public byte* buffer;
    }
}