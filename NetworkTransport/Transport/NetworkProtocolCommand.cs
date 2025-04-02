﻿// ReSharper disable ALL

namespace Network
{
    public enum NetworkProtocolCommand
    {
        NETWORK_PROTOCOL_COMMAND_NONE,

        NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT,
        NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT_ACKNOWLEDGE,

        NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_PING,

        NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT,
        NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT_ACKNOWLEDGE,

        NETWORK_PROTOCOL_COMMAND_RELIABLE_RECEIVE,
        NETWORK_PROTOCOL_COMMAND_UNRELIABLE_RECEIVE,
        NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_RECEIVE,

        NETWORK_PROTOCOL_COMMAND_RELIABLE_RECEIVE_DISCONNECT_LATER
    }
}