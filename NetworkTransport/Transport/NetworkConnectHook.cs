namespace Network
{
    public unsafe struct NetworkConnectHook
    {
        public void* user;
        public required delegate* managed<void*, NetworkHost*, NetworkPeer*, int> hook;
    }
}