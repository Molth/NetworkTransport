using System;
using System.Diagnostics;
using kcp;
using NanoSockets;
using NativeCollections;
using static Network.NetworkEventType;
using static Network.NetworkPacketFlag;
using static Network.NetworkProtocolCommand;
using static Network.NetworkPeerState;
using static kcp.KCP;

// ReSharper disable ALL

namespace Network
{
    public static unsafe class NetworkTransport
    {
        public const uint NETWORK_PEER_PING_INTERVAL_DEFAULT = 500;
        public const uint NETWORK_PEER_TIMEOUT_DEFAULT = 10000;

        public static NetworkHost* network_host_create(NetworkHostCreateOptions options)
        {
            if (options.socketSendBufferSize == 0)
                options.socketSendBufferSize = 8 * 1024 * 1024;

            if (options.socketReceiveBufferSize == 0)
                options.socketReceiveBufferSize = 8 * 1024 * 1024;

            if (options.maximumSocketReceiveSize == 0)
                options.maximumSocketReceiveSize = 1400;
            else if (options.maximumSocketReceiveSize < 128)
                options.maximumSocketReceiveSize = 128;

            if (options.maximumReliableReceiveSize == 0)
                options.maximumReliableReceiveSize = 4096;
            else if (options.maximumReliableReceiveSize < 128)
                options.maximumReliableReceiveSize = 128;

            if (options.eventQueueSize == 0)
                options.eventQueueSize = 128;

            if (options.eventQueueMaxFreeChunks == 0)
                options.eventQueueMaxFreeChunks = 4;

            if (UDP.Initialize() != 0)
                return null;

            var socket = UDP.Create((int)options.socketSendBufferSize, (int)options.socketReceiveBufferSize);
            if (socket == -1)
                return null;

            Address.CreateFromIP("::0", out var address);
            address.Port = options.port;

            if (UDP.Bind(socket, ref address) < 0)
            {
                UDP.Destroy(ref socket);
                return null;
            }

            var host = (NetworkHost*)malloc((uint)sizeof(NetworkHost));

            host->version = options.version;

            host->peerIDs = new UnsafeSparseSet<nint>(options.peerCount);
            host->freeIDs = new UnsafeQueue<ushort>(options.peerCount);
            host->peers = (NetworkPeer*)malloc((uint)(options.peerCount * sizeof(NetworkPeer)));

            memset(host->peers, 0, (uint)(options.peerCount * sizeof(NetworkPeer)));

            for (var i = 0; i < options.peerCount; ++i)
            {
                host->freeIDs.TryEnqueue((ushort)i);

                var peer = &host->peers[i];

                peer->host = host;
                peer->localSession.id = (ushort)i;
            }

            _ = UDP.SetNonBlocking(socket, 1);
            host->socket = socket;

            host->duplicatePeers = options.duplicatePeers;

            host->maximumSocketReceiveSize = options.maximumSocketReceiveSize;
            host->maximumReliableReceiveSize = options.maximumReliableReceiveSize;

            host->incomingEvents = new UnsafeChunkedQueue<NetworkEvent>((int)options.eventQueueSize, (int)options.eventQueueMaxFreeChunks);

            host->connectHook = options.connectHook;

            return host;
        }

        public static void network_host_destroy(NetworkHost* host)
        {
            if (host == null)
                return;

            host->peerIDs.Dispose();

            host->freeIDs.Dispose();

            free(host->peers);

            UDP.Destroy(ref host->socket);
            UDP.Deinitialize();

            while (host->incomingEvents.TryDequeue(out var @event))
            {
                if (@event.type == NETWORK_EVENT_TYPE_RECEIVE)
                    @event.packet.data.Dispose();
            }

            host->incomingEvents.Dispose();

            free(host);
        }

        public static void network_host_ping(NetworkHost* host, Address* address)
        {
            var buffer = stackalloc byte[1];
            _ = UDP.Send(host->socket, ref *address, ref *buffer, 1);
        }

        public static int network_host_service(NetworkHost* host)
        {
            var buffer = stackalloc byte[(int)_imax_(host->maximumSocketReceiveSize, host->maximumReliableReceiveSize)];
            host->buffer = buffer;

            host->serviceTimestamp = (uint)(Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency);

            Address address;
            NetworkSession localSession, remoteSession;

            while (UDP.Poll(host->socket, 0) > 0)
            {
                var byteCount = UDP.Receive(host->socket, ref *(&address), ref *buffer, (int)host->maximumSocketReceiveSize);
                if (byteCount < 0)
                    break;

                if (byteCount < 15)
                    continue;

                uint version;
                memcpy(&version, buffer, 4);
                if (version != host->version)
                    continue;

                switch (*(buffer + 4))
                {
                    case (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT:

                        if (byteCount != 55)
                            continue;

                        memcpy(&remoteSession.id, buffer + 5, 2);
                        memcpy(&remoteSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_connect(host, &address, &remoteSession, buffer + 15);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT_ACKNOWLEDGE:

                        if (byteCount != 33)
                            continue;

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        memcpy(&remoteSession.id, buffer + 15, 2);
                        memcpy(&remoteSession.timestamp, buffer + 17, 8);

                        network_protocol_handle_connect_acknowledge(host, &address, &localSession, &remoteSession, buffer + 25);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_PING:

                        if (byteCount != 15)
                            continue;

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_ping(host, &address, &localSession);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_RELIABLE_RECEIVE:

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_receive_reliable(host, &address, &localSession, buffer + 15, byteCount - 15);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_UNRELIABLE_RECEIVE:

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_receive_unreliable(host, &address, &localSession, buffer + 15, byteCount - 15);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_RECEIVE:

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_receive_unsequenced(host, &address, &localSession, buffer + 15, byteCount - 15);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT:

                        if (byteCount != 15)
                            continue;

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_disconnect(host, &address, &localSession);

                        continue;

                    case (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT_ACKNOWLEDGE:

                        if (byteCount != 15)
                            continue;

                        memcpy(&localSession.id, buffer + 5, 2);
                        memcpy(&localSession.timestamp, buffer + 7, 8);

                        network_protocol_handle_disconnect_acknowledge(host, &address, &localSession);

                        continue;

                    default:
                        continue;
                }
            }

            network_protocol_check_timeouts(host);

            host->buffer = null;

            var eventCount = host->incomingEvents.Count;
            return eventCount > 0 ? eventCount : -1;
        }

        public static int network_host_check_events(NetworkHost* host, NetworkEvent* @event)
        {
            if (host->incomingEvents.TryDequeue(out var value))
            {
                *@event = value;
                return 0;
            }

            return -1;
        }

        public static NetworkPeer* network_host_connect(NetworkHost* host, Address* address, NetworkHostConnectOptions options)
        {
            var peer = network_protocol_add_peer(host, address);

            if (peer == null)
                return null;

            peer->version = options.version;

            peer->state = (byte)NETWORK_PEER_STATE_CONNECTING;

            peer->lastSendTime = host->serviceTimestamp;
            peer->lastReceiveTime = host->serviceTimestamp;

            peer->pingInterval = NETWORK_PEER_PING_INTERVAL_DEFAULT;
            peer->timeout = NETWORK_PEER_TIMEOUT_DEFAULT;

            peer->maximumSocketReceiveSize = host->maximumSocketReceiveSize;
            peer->maximumReliableReceiveSize = host->maximumReliableReceiveSize;

            peer->connectContext = options.connectContext;

            var buffer = stackalloc byte[55];

            memcpy(buffer, &peer->version, 4);

            *(buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT;

            memcpy(buffer + 5, &peer->localSession.id, 2);
            memcpy(buffer + 7, &peer->localSession.timestamp, 8);

            memcpy(buffer + 15, &peer->maximumSocketReceiveSize, 4);
            memcpy(buffer + 19, &peer->maximumReliableReceiveSize, 4);

            memcpy(buffer + 23, &peer->connectContext, 32);

            _ = UDP.Send(host->socket, ref peer->address, ref *buffer, 55);

            return peer;
        }

        private static void network_protocol_handle_connect(NetworkHost* host, Address* address, NetworkSession* remoteSession, byte* buffer)
        {
            uint duplicatePeers = 0;

            NetworkPeer* peer;

            var peers = host->peerIDs.Values;

            for (var i = peers.Count - 1; i >= 0; --i)
            {
                peer = (NetworkPeer*)peers[i];

                if (peer->address == *address)
                {
                    ++duplicatePeers;

                    if (peer->state == (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING &&
                        peer->remoteSession.id == remoteSession->id)
                    {
                        if (peer->remoteSession.timestamp < remoteSession->timestamp)
                        {
                            network_protocol_remove_peer(host, peer);

                            goto add_peer;
                        }

                        return;
                    }
                }
            }

            if (host->duplicatePeers != 0 && duplicatePeers >= host->duplicatePeers)
                return;

            add_peer:

            peer = network_protocol_add_peer(host, address);

            if (peer == null)
                return;

            peer->version = host->version;

            peer->state = (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING;

            peer->remoteSession = *remoteSession;

            peer->lastSendTime = host->serviceTimestamp;
            peer->lastReceiveTime = host->serviceTimestamp;

            peer->pingInterval = NETWORK_PEER_PING_INTERVAL_DEFAULT;
            peer->timeout = NETWORK_PEER_TIMEOUT_DEFAULT;

            memcpy(&peer->maximumSocketReceiveSize, buffer, 4);
            memcpy(&peer->maximumReliableReceiveSize, buffer + 4, 4);

            memcpy(&peer->connectContext, buffer + 8, 32);

            if (host->connectHook.hook != null &&
                host->connectHook.hook(host->connectHook.user, host, peer) != 0)
            {
                network_protocol_remove_peer(host, peer);
                return;
            }

            memcpy(host->buffer, &host->version, 4);

            *(host->buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT_ACKNOWLEDGE;

            memcpy(host->buffer + 5, &peer->remoteSession.id, 2);
            memcpy(host->buffer + 7, &peer->remoteSession.timestamp, 8);

            memcpy(host->buffer + 15, &peer->localSession.id, 2);
            memcpy(host->buffer + 17, &peer->localSession.timestamp, 8);

            memcpy(host->buffer + 25, &host->maximumSocketReceiveSize, 4);
            memcpy(host->buffer + 29, &host->maximumReliableReceiveSize, 4);

            _ = UDP.Send(host->socket, ref peer->address, ref *host->buffer, 33);
        }

        private static void network_protocol_handle_connect_acknowledge(NetworkHost* host, Address* address, NetworkSession* localSession, NetworkSession* remoteSession, byte* buffer)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if (peer->state != (byte)NETWORK_PEER_STATE_CONNECTING ||
                    peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                peer->remoteSession = *remoteSession;

                peer->lastSendTime = host->serviceTimestamp;
                peer->lastReceiveTime = host->serviceTimestamp;

                memcpy(&peer->maximumSocketReceiveSize, buffer, 4);
                memcpy(&peer->maximumReliableReceiveSize, buffer + 4, 4);

                memcpy(host->buffer, &host->version, 4);

                *(host->buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_PING;

                memcpy(host->buffer + 5, &peer->remoteSession.id, 2);
                memcpy(host->buffer + 7, &peer->remoteSession.timestamp, 8);

                _ = UDP.Send(host->socket, ref peer->address, ref *host->buffer, 15);

                network_protocol_connect_notify(host, peer);
            }
        }

        private static void network_protocol_handle_ping(NetworkHost* host, Address* address, NetworkSession* localSession)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if ((peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                     peer->state != (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING &&
                     peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER) ||
                    peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                if (peer->state == (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING)
                    network_protocol_connect_notify(host, peer);

                peer->lastReceiveTime = host->serviceTimestamp;
            }
        }

        private static void network_protocol_handle_disconnect(NetworkHost* host, Address* address, NetworkSession* localSession)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if ((peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                     peer->state != (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING &&
                     peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER) ||
                    peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                peer->state = (byte)NETWORK_PEER_STATE_DISCONNECT_ACKNOWLEDGING;

                peer->lastSendTime = host->serviceTimestamp;
                peer->lastReceiveTime = host->serviceTimestamp;

                memcpy(host->buffer, &host->version, 4);

                *(host->buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT_ACKNOWLEDGE;

                memcpy(host->buffer + 5, &peer->remoteSession.id, 2);
                memcpy(host->buffer + 7, &peer->remoteSession.timestamp, 8);

                _ = UDP.Send(host->socket, ref peer->address, ref *host->buffer, 15);
            }
        }

        private static void network_protocol_handle_disconnect_acknowledge(NetworkHost* host, Address* address, NetworkSession* localSession)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if (peer->state != (byte)NETWORK_PEER_STATE_DISCONNECTING ||
                    peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                network_protocol_disconnect_notify(host, peer);
                network_protocol_remove_peer(host, peer);
            }
        }

        private static void network_protocol_handle_receive_reliable(NetworkHost* host, Address* address, NetworkSession* localSession, byte* buffer, int byteCount)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if ((peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                     peer->state != (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING &&
                     peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                    || peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                if (peer->state == (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING)
                    network_protocol_connect_notify(host, peer);

                peer->lastReceiveTime = host->serviceTimestamp;

                if (ikcp_input(&peer->reliable, buffer, byteCount) < 0)
                {
                    network_protocol_disconnect_notify(host, peer);
                    network_protocol_remove_peer(host, peer);
                    return;
                }

                if (peer->state == (byte)NETWORK_PEER_STATE_DISCONNECT_LATER && iqueue_is_empty(&peer->reliable.snd_buf))
                    network_peer_disconnect(peer);
            }
        }

        private static void network_protocol_handle_receive_unreliable(NetworkHost* host, Address* address, NetworkSession* localSession, byte* buffer, int byteCount)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if ((peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                     peer->state != (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING &&
                     peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                    || peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                if (peer->state == (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING)
                    network_protocol_connect_notify(host, peer);

                peer->lastReceiveTime = host->serviceTimestamp;

                if (byteCount < 4)
                {
                    network_protocol_disconnect_notify(host, peer);
                    network_protocol_remove_peer(host, peer);

                    return;
                }

                uint sequenceNumber;
                memcpy(&sequenceNumber, buffer, 4);

                buffer += 4;
                byteCount -= 4;

                if (_itimediff(sequenceNumber, peer->unreliableReceiveSequenceNumber) <= 0)
                    return;

                peer->unreliableReceiveSequenceNumber = sequenceNumber;

                var data = new NativeArray<byte>(byteCount);
                memcpy(data.Array, buffer, (nuint)byteCount);

                var packet = new NetworkPacket
                {
                    flag = NETWORK_PACKET_FLAG_UNRELIABLE,
                    data = data
                };

                host->incomingEvents.Enqueue(new NetworkEvent
                {
                    type = NETWORK_EVENT_TYPE_RECEIVE,
                    peer = peer,
                    packet = packet,
                    guid = peer->guid
                });
            }
        }

        private static void network_protocol_handle_receive_unsequenced(NetworkHost* host, Address* address, NetworkSession* localSession, byte* buffer, int byteCount)
        {
            if (host->peerIDs.TryGetValue(localSession->id, out var value))
            {
                var peer = (NetworkPeer*)value;

                if ((peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                     peer->state != (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING &&
                     peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                    || peer->address != *address || peer->localSession.timestamp != localSession->timestamp)
                    return;

                if (peer->state == (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING)
                    network_protocol_connect_notify(host, peer);

                peer->lastReceiveTime = host->serviceTimestamp;

                if (byteCount == 0)
                    return;

                var data = new NativeArray<byte>(byteCount);
                memcpy(data.Array, buffer, (nuint)byteCount);

                var packet = new NetworkPacket
                {
                    flag = NETWORK_PACKET_FLAG_UNSEQUENCED,
                    data = data
                };

                host->incomingEvents.Enqueue(new NetworkEvent
                {
                    type = NETWORK_EVENT_TYPE_RECEIVE,
                    peer = peer,
                    packet = packet,
                    guid = peer->guid
                });
            }
        }

        private static NetworkPeer* network_protocol_add_peer(NetworkHost* host, Address* address)
        {
            NetworkPeer* peer = null;
            if (host->freeIDs.TryDequeue(out var peerID))
            {
                peer = &host->peers[peerID];
                host->peerIDs.Insert(peerID, (nint)peer);

                peer->address = *address;

                peer->localSession.timestamp = DateTime.UtcNow.Ticks;
            }

            return peer;
        }

        private static void network_protocol_remove_peer(NetworkHost* host, NetworkPeer* peer)
        {
            host->freeIDs.TryEnqueue(peer->localSession.id);
            host->peerIDs.Remove(peer->localSession.id);
        }

        private static void network_protocol_connect_notify(NetworkHost* host, NetworkPeer* peer)
        {
            peer->state = (byte)NETWORK_PEER_STATE_CONNECTED;

            ikcp_create(&peer->reliable, peer);
            ikcp_setoutput(&peer->reliable, &network_protocol_reliable_output);

            ikcp_nodelay(&peer->reliable, 1, 10, 2, 1);
            ikcp_wndsize(&peer->reliable, 128, 256);
            ikcp_setmtu(&peer->reliable, (int)host->maximumSocketReceiveSize - 15);

            peer->unreliableSendSequenceNumber = 0;
            peer->unreliableReceiveSequenceNumber = 0;

            peer->guid = Guid.NewGuid();

            host->incomingEvents.Enqueue(new NetworkEvent
            {
                type = NETWORK_EVENT_TYPE_CONNECT,
                peer = peer,
                guid = peer->guid
            });
        }

        private static void network_protocol_disconnect_notify(NetworkHost* host, NetworkPeer* peer)
        {
            peer->state = (byte)NETWORK_PEER_STATE_NONE;

            ikcp_release(&peer->reliable);

            host->incomingEvents.Enqueue(new NetworkEvent
            {
                type = NETWORK_EVENT_TYPE_DISCONNECT,
                peer = peer,
                guid = peer->guid
            });
        }

        private static void network_protocol_check_timeouts(NetworkHost* host)
        {
            var peers = host->peerIDs.Values;

            for (var i = peers.Count - 1; i >= 0; --i)
            {
                var peer = (NetworkPeer*)peers[i];

                if (_itimediff(peer->lastReceiveTime + peer->timeout, host->serviceTimestamp) <= 0)
                {
                    if (peer->state == (byte)NETWORK_PEER_STATE_CONNECTED ||
                        peer->state == (byte)NETWORK_PEER_STATE_CONNECTING ||
                        peer->state == (byte)NETWORK_PEER_STATE_DISCONNECTING ||
                        peer->state == (byte)NETWORK_PEER_STATE_DISCONNECT_ACKNOWLEDGING ||
                        peer->state == (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                        network_protocol_disconnect_notify(host, peer);

                    network_protocol_remove_peer(host, peer);

                    goto next_peer;
                }

                if (_itimediff(peer->lastSendTime + peer->pingInterval, host->serviceTimestamp) <= 0)
                {
                    peer->lastSendTime = host->serviceTimestamp;

                    byte command;
                    NetworkSession* session;

                    switch (peer->state)
                    {
                        case (byte)NETWORK_PEER_STATE_CONNECTING:

                            command = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT;
                            session = &peer->localSession;

                            break;

                        case (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING:

                            command = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_CONNECT_ACKNOWLEDGE;
                            session = &peer->remoteSession;

                            break;

                        case (byte)NETWORK_PEER_STATE_DISCONNECTING:

                            command = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT;
                            session = &peer->remoteSession;

                            break;

                        case (byte)NETWORK_PEER_STATE_DISCONNECT_ACKNOWLEDGING:

                            command = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT_ACKNOWLEDGE;
                            session = &peer->remoteSession;

                            break;

                        default:

                            command = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_PING;
                            session = &peer->remoteSession;

                            break;
                    }

                    memcpy(host->buffer, &host->version, 4);
                    *(host->buffer + 4) = command;
                    memcpy(host->buffer + 5, &session->id, 2);
                    memcpy(host->buffer + 7, &session->timestamp, 8);

                    if (peer->state == (byte)NETWORK_PEER_STATE_CONNECTING)
                    {
                        memcpy(host->buffer + 15, &peer->maximumSocketReceiveSize, 4);
                        memcpy(host->buffer + 19, &peer->maximumReliableReceiveSize, 4);

                        memcpy(host->buffer + 23, &peer->connectContext, 32);

                        _ = UDP.Send(host->socket, ref peer->address, ref *host->buffer, 55);
                    }
                    else if (peer->state == (byte)NETWORK_PEER_STATE_CONNECT_ACKNOWLEDGING)
                    {
                        memcpy(host->buffer + 15, &peer->localSession.id, 2);
                        memcpy(host->buffer + 17, &peer->localSession.timestamp, 8);

                        memcpy(host->buffer + 25, &host->maximumSocketReceiveSize, 4);
                        memcpy(host->buffer + 29, &host->maximumReliableReceiveSize, 4);

                        _ = UDP.Send(host->socket, ref peer->address, ref *host->buffer, 33);
                    }
                    else
                        _ = UDP.Send(host->socket, ref peer->address, ref *host->buffer, 15);
                }

                if (peer->state == (byte)NETWORK_PEER_STATE_CONNECTED ||
                    peer->state == (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                {
                    memcpy(host->buffer, &host->version, 4);
                    *(host->buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_RELIABLE_RECEIVE;
                    memcpy(host->buffer + 5, &peer->remoteSession.id, 2);
                    memcpy(host->buffer + 7, &peer->remoteSession.timestamp, 8);

                    ikcp_update(&peer->reliable, host->serviceTimestamp, host->buffer + 15);

                    while (true)
                    {
                        var byteCount = ikcp_recv(&peer->reliable, host->buffer, (int)host->maximumReliableReceiveSize);

                        if (byteCount == -1)
                            break;

                        if (byteCount < 0)
                        {
                            network_protocol_disconnect_notify(host, peer);
                            network_protocol_remove_peer(host, peer);

                            goto next_peer;
                        }

                        var data = new NativeArray<byte>(byteCount);
                        memcpy(data.Array, host->buffer, (nuint)byteCount);

                        var packet = new NetworkPacket
                        {
                            flag = NETWORK_PACKET_FLAG_RELIABLE,
                            data = data
                        };

                        host->incomingEvents.Enqueue(new NetworkEvent
                        {
                            type = NETWORK_EVENT_TYPE_RECEIVE,
                            peer = peer,
                            packet = packet,
                            guid = peer->guid
                        });
                    }
                }

                next_peer: ;
            }
        }

        private static int network_protocol_reliable_output(byte* buffer, int byteCount, IKCPCB* __, void* user)
        {
            var peer = (NetworkPeer*)user;

            peer->lastSendTime = peer->host->serviceTimestamp;

            _ = UDP.Send(peer->host->socket, ref peer->address, ref *(buffer - 15), 15 + byteCount);

            return 0;
        }

        public static int network_peer_disconnect(NetworkPeer* peer)
        {
            if (peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                peer->state != (byte)NETWORK_PEER_STATE_CONNECTING &&
                peer->state != (byte)NETWORK_PEER_STATE_DISCONNECTING &&
                peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                return -1;

            if (peer->state == (byte)NETWORK_PEER_STATE_CONNECTED ||
                peer->state == (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
            {
                peer->state = (byte)NETWORK_PEER_STATE_DISCONNECTING;

                ikcp_release(&peer->reliable);

                peer->lastSendTime = peer->host->serviceTimestamp;

                var buffer = stackalloc byte[15];

                memcpy(buffer, &peer->host->version, 4);

                *(buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT;

                memcpy(buffer + 5, &peer->remoteSession.id, 2);
                memcpy(buffer + 7, &peer->remoteSession.timestamp, 8);

                _ = UDP.Send(peer->host->socket, ref peer->address, ref *buffer, 15);
            }
            else
            {
                network_protocol_disconnect_notify(peer->host, peer);
                network_protocol_remove_peer(peer->host, peer);
            }

            return 0;
        }

        public static int network_peer_disconnect_now(NetworkPeer* peer)
        {
            if (peer->state != (byte)NETWORK_PEER_STATE_CONNECTED &&
                peer->state != (byte)NETWORK_PEER_STATE_CONNECTING &&
                peer->state != (byte)NETWORK_PEER_STATE_DISCONNECTING &&
                peer->state != (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
                return -1;

            if (peer->state == (byte)NETWORK_PEER_STATE_CONNECTED ||
                peer->state == (byte)NETWORK_PEER_STATE_DISCONNECT_LATER)
            {
                var buffer = stackalloc byte[15];

                memcpy(buffer, &peer->host->version, 4);

                *(buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_DISCONNECT;

                memcpy(buffer + 5, &peer->remoteSession.id, 2);
                memcpy(buffer + 7, &peer->remoteSession.timestamp, 8);

                _ = UDP.Send(peer->host->socket, ref peer->address, ref *buffer, 15);
            }

            network_protocol_disconnect_notify(peer->host, peer);
            network_protocol_remove_peer(peer->host, peer);

            return 0;
        }

        public static int network_peer_disconnect_later(NetworkPeer* peer)
        {
            if (peer->state != (byte)NETWORK_PEER_STATE_CONNECTED)
                return -1;

            if (iqueue_is_empty(&peer->reliable.snd_buf))
                return network_peer_disconnect(peer);

            peer->state = (byte)NETWORK_PEER_STATE_DISCONNECT_LATER;

            return 0;
        }

        public static void network_peer_ping_interval(NetworkPeer* peer, uint pingInterval, uint timeout)
        {
            if (pingInterval != 0)
                peer->pingInterval = pingInterval;

            if (timeout != 0)
                peer->timeout = timeout;
        }

        public static int network_peer_send(NetworkPeer* peer, void* data, int length, NetworkPacketFlag flag)
        {
            if (peer->state != (byte)NETWORK_PEER_STATE_CONNECTED || length <= 0)
                return -1;

            switch (flag)
            {
                case NETWORK_PACKET_FLAG_RELIABLE:
                    return network_peer_send_reliable(peer, data, length);

                case NETWORK_PACKET_FLAG_UNRELIABLE:
                    return network_peer_send_unreliable(peer, data, length);

                case NETWORK_PACKET_FLAG_UNSEQUENCED:
                    return network_peer_send_unsequenced(peer, data, length);

                default:
                    return -1;
            }
        }

        private static int network_peer_send_reliable(NetworkPeer* peer, void* data, int length)
        {
            if (length > (int)peer->maximumReliableReceiveSize)
                return -1;

            return ikcp_send(&peer->reliable, (byte*)data, length);
        }

        private static int network_peer_send_unreliable(NetworkPeer* peer, void* data, int length)
        {
            if (19 + length > (int)peer->maximumSocketReceiveSize)
                return -1;

            peer->lastSendTime = peer->host->serviceTimestamp;

            var buffer = stackalloc byte[19 + length];

            memcpy(buffer, &peer->host->version, 4);

            *(buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNRELIABLE_RECEIVE;

            memcpy(buffer + 5, &peer->remoteSession.id, 2);
            memcpy(buffer + 7, &peer->remoteSession.timestamp, 8);

            ++peer->unreliableSendSequenceNumber;
            memcpy(buffer + 15, &peer->unreliableSendSequenceNumber, 4);

            memcpy(buffer + 19, data, (nuint)length);

            _ = UDP.Send(peer->host->socket, ref peer->address, ref *buffer, 19 + length);

            return 0;
        }

        private static int network_peer_send_unsequenced(NetworkPeer* peer, void* data, int length)
        {
            if (15 + length > (int)peer->maximumSocketReceiveSize)
                return -1;

            peer->lastSendTime = peer->host->serviceTimestamp;

            var buffer = stackalloc byte[15 + length];

            memcpy(buffer, &peer->host->version, 4);

            *(buffer + 4) = (byte)NETWORK_PROTOCOL_COMMAND_UNSEQUENCED_RECEIVE;

            memcpy(buffer + 5, &peer->remoteSession.id, 2);
            memcpy(buffer + 7, &peer->remoteSession.timestamp, 8);

            memcpy(buffer + 15, data, (nuint)length);

            _ = UDP.Send(peer->host->socket, ref peer->address, ref *buffer, 15 + length);

            return 0;
        }
    }
}