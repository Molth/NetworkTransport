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

        public NetworkPeer* peers;

        public Socket socket;
        public Address address;

        public uint duplicatePeers;

        public uint maximumSocketReceiveSize;
        public uint maximumReliableReceiveSize;

        public UnsafeChunkedQueue<NetworkEvent> incomingEvents;

        public NetworkConnectHook connectHook;

        public uint servicePeers;
        public uint connectedPeers;

        public uint freeHead;
        public uint freeTail;
        public uint freeCount;

        public byte* buffer;
    }
}