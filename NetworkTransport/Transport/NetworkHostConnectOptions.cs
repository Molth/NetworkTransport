// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkHostConnectOptions
    {
        public required uint version;
        public NetworkConnectContext connectContext;
    }
}