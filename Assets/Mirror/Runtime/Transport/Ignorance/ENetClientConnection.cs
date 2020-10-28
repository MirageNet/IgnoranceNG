#region Statements

using System;
using Cysharp.Threading.Tasks;
using ENet;
using UnityEngine;
using Event = ENet.Event;
using EventType = ENet.EventType;

#endregion

namespace Mirror.ENet
{
    public class ENetClientConnection : Common
    {
        #region Fields

        private readonly Host _clientHost;
        private static volatile PeerStatistics _statistics = new PeerStatistics();
        private readonly int _pingUpdateInterval;
        private IgnoranceIncomingMessage _incomingIgnoranceMessage;

        #endregion

        /// <summary>
        ///     Initialize Constructor.
        /// </summary>
        /// <param name="client">The peer we are connecting with or to.</param>
        /// <param name="host">The host we are connecting with or to.</param>
        /// <param name="config">The configuration file to be used for all client connections.</param>
        public ENetClientConnection(Peer client, Host host, Configuration config) : base(client, config)
        {
            _clientHost = host;
            _statistics = new PeerStatistics();
            _pingUpdateInterval = Config.StatisticsCalculationInterval;
        }

        /// <summary>
        ///     Process all incoming messages and queue them up for mirror.
        /// </summary>
        protected override async UniTaskVoid ProcessMessages()
        {
            // Only process messages if the client is valid.
            while (!CancelToken.IsCancellationRequested)
            {
                // Setup...
                uint nextStatsUpdate = 0;

                // Only process messages if the client is valid.
                while (!CancelToken.IsCancellationRequested)
                {
                    bool clientWasPolled = false;

                    if (Library.Time >= nextStatsUpdate)
                    {
                        _statistics.CurrentPing = Client.RoundTripTime;
                        _statistics.BytesReceived = Client.BytesReceived;
                        _statistics.BytesSent = Client.BytesSent;

                        _statistics.PacketsLost = Client.PacketsLost;
                        _statistics.PacketsSent = Client.PacketsSent;

                        // Library.Time is milliseconds, so we need to do some quick math.
                        nextStatsUpdate = Library.Time + (uint)(_pingUpdateInterval * 1000);
                    }

                    while (!clientWasPolled)
                    {
                        if (_clientHost.CheckEvents(out Event networkEvent) <= 0)
                        {
                            if (_clientHost.Service(Config.EnetPollTimeout, out networkEvent) <= 0) break;
                            clientWasPolled = true;
                        }

                        switch (networkEvent.Type)
                        {
                            case EventType.Timeout:
                            case EventType.Disconnect:

                                if (Config.DebugEnabled) Debug.Log($"Ignorance: Dead Peer. {networkEvent.Peer.ID}.");

                                Disconnect();

                                networkEvent.Packet.Dispose();

                                break;
                            case EventType.Receive:
                                // Client recieving some data.
                                if (Client.ID != networkEvent.Peer.ID)
                                {
                                    // Emit a warning and clean the packet. We don't want it in memory.
                                    if (Config.DebugEnabled)
                                        Debug.LogWarning(
                                            $"Ignorance: Unknown packet from Peer {networkEvent.Peer.ID}. Be cautious - if you get this error too many times, you're likely being attacked.");
                                    networkEvent.Packet.Dispose();
                                    break;
                                }

                                if (!networkEvent.Packet.IsSet)
                                {
                                    if (Config.DebugEnabled)
                                        Debug.LogWarning("Ignorance WARNING: A incoming packet is not set correctly.");
                                    break;
                                }

                                if (networkEvent.Packet.Length > Config.PacketCache.Length)
                                {
                                    if (Config.DebugEnabled)
                                        Debug.LogWarning(
                                            $"Ignorance: Packet too big to fit in buffer. {networkEvent.Packet.Length} packet bytes vs {Config.PacketCache.Length} cache bytes {networkEvent.Peer.ID}.");
                                    networkEvent.Packet.Dispose();
                                }
                                else
                                {
                                    // invoke on the client.
                                    try
                                    {
                                        _incomingIgnoranceMessage =
                                            new IgnoranceIncomingMessage
                                            {
                                                ChannelId = networkEvent.ChannelID,
                                                Data = new byte[networkEvent.Packet.Length]
                                            };

                                        networkEvent.Packet.CopyTo(_incomingIgnoranceMessage.Data);

                                        IncomingQueuedData.Enqueue(_incomingIgnoranceMessage);

                                        if (Config.DebugEnabled)
                                            Debug.Log(
                                                $"Ignorance: Queuing up incoming data packet: {BitConverter.ToString(_incomingIgnoranceMessage.Data)}");
                                    }
                                    catch (Exception e)
                                    {
                                        Debug.LogError(
                                            $"Ignorance caught an exception while trying to copy data from the unmanaged (ENET) world to managed (Mono/IL2CPP) world. Please consider reporting this to the Ignorance developer on GitHub.\n" +
                                            $"Exception returned was: {e.Message}\n" +
                                            $"Debug details: {(Config.PacketCache == null ? "packet buffer was NULL" : $"{Config.PacketCache.Length} byte work buffer")}, {networkEvent.Packet.Length} byte(s) network packet length\n" +
                                            $"Stack Trace: {e.StackTrace}");
                                    }
                                }

                                networkEvent.Packet.Dispose();

                                break;
                            default:
                                networkEvent.Packet.Dispose();
                                break;
                        }
                    }

                    while (OutgoingQueuedData.TryDequeue(out IgnoranceOutgoingMessage message))
                    {
                        int returnCode = Client.Send(message.ChannelId, ref message.Payload);

                        if (returnCode == 0)
                        {
                            if (Config.DebugEnabled)
                                Debug.Log(
                                    $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} OK");

                            await UniTask.Delay(1);

                            continue;
                        }

                        if (Config.DebugEnabled)
                            Debug.Log(
                                $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} FAIL, code {returnCode}");

                        await UniTask.Delay(1);
                    }

                    await UniTask.Delay(1);
                }
            }
        }
    }
}
