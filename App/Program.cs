using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NanoSockets;
using static Network.NetworkEventType;
using static Network.NetworkTransport;

// ReSharper disable ALL

namespace Network
{
    public static unsafe class Program
    {
        private static bool _isRunning;

        private static void Main() => Start();

        private static void Start()
        {
            _isRunning = true;
            Console.CancelKeyPress += (sender, args) => _isRunning = false;

            new Thread(StartServer) { IsBackground = true }.Start();
            new Thread(StartClient) { IsBackground = true }.Start();

            while (_isRunning)
                Thread.Sleep(1000);
        }

        private static void StartServer()
        {
            var host = network_host_create(new NetworkHostCreateOptions
            {
                version = 1,
                peerCount = 4,
                port = 7777,
                eventQueueSize = 4
            });

            network_host_service(host);

            NetworkEvent @event;

            while (_isRunning)
            {
                if (network_host_service(host) > 0)
                {
                    while (network_host_check_events(host, &@event) == 0)
                    {
                        switch (@event.type)
                        {
                            case NETWORK_EVENT_TYPE_NONE:
                                break;

                            case NETWORK_EVENT_TYPE_CONNECT:

                                network_peer_ping_interval(@event.peer, NETWORK_PEER_PING_INTERVAL_DEFAULT, 1000);

                                Console.WriteLine("server connected");

                                break;

                            case NETWORK_EVENT_TYPE_RECEIVE:

                                Console.WriteLine("[server]" + @event.peer->localSession.id + ": " + Encoding.UTF8.GetString(@event.packet.data));
                                @event.packet.data.Dispose();

                                break;

                            case NETWORK_EVENT_TYPE_DISCONNECT:

                                Console.WriteLine("server disconnected");

                                break;
                        }
                    }
                }

                Thread.Sleep(100);
            }
        }

        private static void StartClient()
        {
            var host = network_host_create(new NetworkHostCreateOptions
            {
                version = 1,
                peerCount = 4,
                port = 0
            });

            ushort i = 0;

            Address.CreateFromIP("127.0.0.1", out var address);
            address.Port = 7777;

            network_host_service(host);
            network_host_connect(host, &address, new NetworkHostConnectOptions()
            {
                version = 1
            });

            NetworkEvent @event;

            NetworkPeer* peer = null;

            var buffer = stackalloc byte[1024];

            var disconnected = false;

            while (_isRunning)
            {
                if (network_host_service(host) > 0)
                {
                    while (network_host_check_events(host, &@event) == 0)
                    {
                        switch (@event.type)
                        {
                            case NETWORK_EVENT_TYPE_NONE:
                                break;

                            case NETWORK_EVENT_TYPE_CONNECT:

                                peer = @event.peer;

                                network_peer_ping_interval(peer, NETWORK_PEER_PING_INTERVAL_DEFAULT, 1000);

                                Console.WriteLine("client connected");

                                break;

                            case NETWORK_EVENT_TYPE_RECEIVE:

                                Console.WriteLine("[client]" + @event.peer->localSession.id + ": " + Encoding.UTF8.GetString(@event.packet.data));
                                @event.packet.data.Dispose();

                                break;

                            case NETWORK_EVENT_TYPE_DISCONNECT:

                                peer = null;

                                Console.WriteLine("client disconnected");

                                break;
                        }
                    }
                }

                if (!disconnected && peer != null)
                {
                    if (i == 10000)
                    {
                        network_peer_disconnect_later(peer);

                        disconnected = true;
                    }
                    else
                    {
                        var byteCount = Encoding.UTF8.GetBytes($"test {i++}", MemoryMarshal.CreateSpan(ref *buffer, 1024));
                        network_peer_send(peer, buffer, byteCount, NetworkPacketFlag.NETWORK_PACKET_FLAG_RELIABLE);
                    }
                }

                Thread.Sleep(100);
            }
        }
    }
}