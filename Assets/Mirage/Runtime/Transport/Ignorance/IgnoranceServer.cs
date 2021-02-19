#region Statements

using System;
using System.Collections.Generic;
using System.Net;
using Cysharp.Threading.Tasks;
using ENet;
using UnityEngine;
using Event = ENet.Event;
using EventType = ENet.EventType;

#endregion

namespace Mirage.ENet
{
    public class IgnoranceServer
    {
        #region Fields

        private readonly Configuration _config;
        private readonly Transport _transport;
        private Host _enetHost;
        private Address _enetAddress;
        public bool ServerStarted;
        private readonly Dictionary<uint, ENetClientConnection> _connectedClients = new Dictionary<uint, ENetClientConnection>();

        #endregion

        /// <summary>
        ///     Initialize constructor.
        /// </summary>
        /// <param name="config"></param>
        public IgnoranceServer(Configuration config, Transport transport)
        {
            _config = config;
            _transport = transport;
            _enetHost = new Host();
        }

        /// <summary>
        ///     Processes and accepts new incoming connections.
        /// </summary>
        /// <returns></returns>
        private async UniTaskVoid AcceptConnections()
        {
            while (ServerStarted)
            {
                bool serverWasPolled = false;

                while (!serverWasPolled)
                {
                    if (_enetHost.CheckEvents(out Event networkEvent) <= 0)
                    {
                        if (_enetHost.Service(_config.EnetPollTimeout, out networkEvent) <= 0) break;

                        serverWasPolled = true;
                    }

                    _connectedClients.TryGetValue(networkEvent.Peer.ID, out ENetClientConnection client);

                    switch (networkEvent.Type)
                    {
                        case EventType.Connect:

                            // A client connected to the server. Assign a new ID to them.
                            if (_config.DebugEnabled)
                            {
                                Debug.Log(
                                    $"Ignorance: New connection from {networkEvent.Peer.IP}:{networkEvent.Peer.Port}");
                                Debug.Log(
                                    $"Ignorance: Map {networkEvent.Peer.IP}:{networkEvent.Peer.Port} (ENET Peer {networkEvent.Peer.ID})");
                            }

                            if (_config.CustomTimeoutLimit)
                                networkEvent.Peer.Timeout(Library.throttleScale, _config.CustomTimeoutBaseTicks,
                                    _config.CustomTimeoutBaseTicks * _config.CustomTimeoutMultiplier);

                            var connection = new ENetClientConnection(networkEvent.Peer, _enetHost, _config, true);

                            _connectedClients.Add(networkEvent.Peer.ID, connection);

                            _transport.Connected.Invoke(connection);

                            break;
                        case EventType.Timeout:
                        case EventType.Disconnect:

                            if(!(client is null))
                            {
                                if (_config.DebugEnabled) Debug.Log($"Ignorance: Dead Peer. {networkEvent.Peer.ID}.");

                                client.Disconnect();

                                _connectedClients.Remove(networkEvent.Peer.ID);
                            }
                            else
                            {
                                if (_config.DebugEnabled)
                                    Debug.LogWarning(
                                        "Ignorance: Peer is already dead, received another disconnect message.");
                            }

                            networkEvent.Packet.Dispose();

                            break;
                        case EventType.Receive:

                            // Client recieving some data.
                            if (client?.Client.ID != networkEvent.Peer.ID)
                            {
                                // Emit a warning and clean the packet. We don't want it in memory.
                                if (_config.DebugEnabled)
                                    Debug.LogWarning(
                                        $"Ignorance: Unknown packet from Peer {networkEvent.Peer.ID}. Be cautious - if you get this error too many times, you're likely being attacked.");
                                networkEvent.Packet.Dispose();
                                break;
                            }

                            if (!networkEvent.Packet.IsSet)
                            {
                                if (_config.DebugEnabled)
                                    Debug.LogWarning("Ignorance WARNING: A incoming packet is not set correctly.");
                                break;
                            }

                            if (networkEvent.Packet.Length > _config.PacketCache.Length)
                            {
                                if (_config.DebugEnabled)
                                    Debug.LogWarning(
                                        $"Ignorance: Packet too big to fit in buffer. {networkEvent.Packet.Length} packet bytes vs {_config.PacketCache.Length} cache bytes {networkEvent.Peer.ID}.");
                                networkEvent.Packet.Dispose();
                            }
                            else
                            {
                                // invoke on the client.
                                try
                                {
                                    var incomingIgnoranceMessage =
                                        new IgnoranceIncomingMessage
                                        {
                                            ChannelId = networkEvent.ChannelID,
                                            Data = new byte[networkEvent.Packet.Length]
                                        };

                                    networkEvent.Packet.CopyTo(incomingIgnoranceMessage.Data);

                                    client.IncomingQueuedData.Enqueue(incomingIgnoranceMessage);

                                    if (_config.DebugEnabled)
                                        Debug.Log(
                                            $"Ignorance: Queuing up incoming data packet: {BitConverter.ToString(incomingIgnoranceMessage.Data)}");
                                }
                                catch (Exception e)
                                {
                                    Debug.LogError(
                                        $"Ignorance caught an exception while trying to copy data from the unmanaged (ENET) world to managed (Mono/IL2CPP) world. Please consider reporting this to the Ignorance developer on GitHub.\n" +
                                        $"Exception returned was: {e.Message}\n" +
                                        $"Debug details: {(_config.PacketCache == null ? "packet buffer was NULL" : $"{_config.PacketCache.Length} byte work buffer")}, {networkEvent.Packet.Length} byte(s) network packet length\n" +
                                        $"Stack Trace: {e.StackTrace}");
                                }
                            }

                            networkEvent.Packet.Dispose();

                            break;
                        default:
                            networkEvent.Packet.Dispose();
                            break;
                    }
                    
                    await UniTask.Delay(10);
                }

                foreach (KeyValuePair<uint, ENetClientConnection> connectedClient in _connectedClients)
                {
                    while (connectedClient.Value.OutgoingQueuedData.TryDequeue(out IgnoranceOutgoingMessage message))
                    {
                        int returnCode = connectedClient.Value.Client.Send(message.ChannelId, ref message.Payload);

                        if (returnCode == 0)
                        {
                            if (_config.DebugEnabled)
                                Debug.Log(
                                    $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} OK");

                            continue;
                        }

                        if (_config.DebugEnabled)
                            Debug.Log(
                                $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} FAIL, code {returnCode}");
                    }
                }

                await UniTask.Delay(10);
            }
        }

        /// <summary>
        ///     Shutdown the server and cleanup.
        /// </summary>
        public void Shutdown()
        {
            if (_config.DebugEnabled)
            {
                Debug.Log("[DEBUGGING MODE] Ignorance: ServerStop()");
                Debug.Log("[DEBUGGING MODE] Ignorance: Cleaning the packet cache...");
            }

            ServerStarted = false;

            _enetHost.Flush();
            _enetHost.Dispose();
        }

        /// <summary>
        ///     Start up the server and initialize things
        /// </summary>
        /// <returns></returns>
        public void Start()
        {
            if (!_config.ServerBindAll)
            {
                if (_config.DebugEnabled)
                    Debug.Log(
                        "Ignorance: Not binding to all interfaces, checking if supplied info is actually an IP address");

                if (IPAddress.TryParse(_config.ServerBindAddress, out _))
                {
                    // Looks good to us. Let's use it.
                    if (_config.DebugEnabled) Debug.Log($"Ignorance: Valid IP Address {_config.ServerBindAddress}");

                    _enetAddress.SetIP(_config.ServerBindAddress);
                }
                else
                {
                    // Might be a hostname.
                    if (_config.DebugEnabled)
                        Debug.Log("Ignorance: Doesn't look like a valid IP address, assuming it's a hostname?");

                    _enetAddress.SetHost(_config.ServerBindAddress);
                }
            }
            else
            {
                if (_config.DebugEnabled)
                    Debug.Log($"Ignorance: Setting address to all interfaces, port {_config.CommunicationPort}");
#if UNITY_IOS
                // Coburn: temporary fix until I figure out if this is similar to the MacOS bug again...
                ENETAddress.SetIP("::0");
#endif
            }

            _enetAddress.Port = (ushort) _config.CommunicationPort;

            if (_enetHost == null || !_enetHost.IsSet) _enetHost = new Host();

            // Go go go! Clear those corners!
            _enetHost.Create(_enetAddress, _config.CustomMaxPeerLimit ? _config.CustomMaxPeers : (int) Library.maxPeers,
                _config.Channels.Length, 0, 0);

            if (_config.DebugEnabled)
                Debug.Log(
                    "[DEBUGGING MODE] Ignorance: Server should be created now... If Ignorance immediately crashes after this line, please file a bug report on the GitHub.");

            ServerStarted = true;

            UniTask.Run(AcceptConnections).Forget();

            // Transport has finish starting.
            _transport.Started.Invoke();
        }
    }
}
