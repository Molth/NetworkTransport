using NanoSockets;
using NativeCollections;

// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkHost
    {
        public uint version;

        public uint serviceTimestamp;

        public UnsafeSparseSet<nint> peerIDs;
        public UnsafeQueue<ushort> freeIDs;
        public NetworkPeer* peers;

        public Socket socket;
        public UnsafeChunkedQueue<NetworkEvent> incomingEvents;
    }
}