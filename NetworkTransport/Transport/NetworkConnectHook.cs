using NanoSockets;

// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkConnectHook
    {
        public void* user;
        public required delegate* managed<void*, NetworkHost*, Address*, int> addressHook;
        public required delegate* managed<void*, NetworkHost*, NetworkPeer*, int> peerHook;
    }
}