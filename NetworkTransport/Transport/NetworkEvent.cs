﻿using System;

// ReSharper disable ALL

namespace Network
{
    public unsafe struct NetworkEvent
    {
        public required NetworkEventType type;
        public required NetworkPeer* peer;
        public NetworkPacket packet;
        public required Guid guid;
    }
}