using NanoSockets;
using NativeCollections;

// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkHost
    {
        public uint version;

        public ushort peerCount;

        public uint serviceTimestamp;

        public UnsafeSparseSet<nint> peerIDs;
        public NetworkPeer* peers;

        public Socket socket;
        public Address address;

        public uint duplicatePeers;

        public uint maximumSocketReceiveSize;
        public uint maximumReliableReceiveSize;

        public UnsafeChunkedQueue<NetworkEvent> incomingEvents;

        public uint connectedPeers;

        public NetworkConnectHook connectHook;

        public ushort freeHead;
        public ushort freeTail;
        public ushort freeCount;

        public byte* buffer;
    }
}