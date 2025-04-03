using System;
using kcp;
using NanoSockets;

// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkPeer
    {
        public NetworkHost* host;

        public ushort freeID;

        public uint version;

        public Address address;

        public byte state;

        public NetworkSession localSession;
        public NetworkSession remoteSession;

        public uint lastSendTime;
        public uint lastReceiveTime;

        public uint pingInterval;
        public uint timeout;

        public uint maximumSocketReceiveSize;
        public uint maximumReliableReceiveSize;

        public NetworkConnectContext connectContext;

        public ulong sentDataTotal;
        public ulong receivedDataTotal;

        public IKCPCB reliable;

        public uint unreliableSendSequenceNumber;
        public uint unreliableReceiveSequenceNumber;

        public Guid guid;
    }
}