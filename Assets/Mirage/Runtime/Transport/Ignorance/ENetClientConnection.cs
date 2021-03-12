#region Statements

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using ENet;
using UnityEngine;
using Event = ENet.Event;
using EventType = ENet.EventType;

#endregion

namespace Mirage.ENet
{
    public class ENetClientConnection : IConnection
    {
        #region Fields

        private readonly Host _clientHost;
        private static volatile PeerStatistics _statistics = new PeerStatistics();
        private readonly int _pingUpdateInterval;
        private bool _serverConnection;

        internal Peer Client;

        internal readonly ConcurrentQueue<IgnoranceIncomingMessage> IncomingQueuedData =
            new ConcurrentQueue<IgnoranceIncomingMessage>();

        internal readonly ConcurrentQueue<IgnoranceOutgoingMessage> OutgoingQueuedData =
            new ConcurrentQueue<IgnoranceOutgoingMessage>();

        internal readonly Configuration Config;
        internal readonly CancellationTokenSource CancelToken;

        #endregion

        /// <summary>
        ///     Initialize Constructor.
        /// </summary>
        /// <param name="client">The peer we are connecting with or to.</param>
        /// <param name="host">The host we are connecting with or to.</param>
        /// <param name="config">The configuration file to be used for all client connections.</param>
        /// <param name="isServer">Whether or not this class is being used for server connections.</param>
        public ENetClientConnection(Peer client, Host host, Configuration config, bool isServer)
        {
            _clientHost = host;
            _statistics = new PeerStatistics();
            _pingUpdateInterval = config.StatisticsCalculationInterval;

            Config = config;
            Client = client;
            CancelToken = new CancellationTokenSource();
            _serverConnection = isServer;

            if (isServer) return;

            UniTask.Run(ProcessMessages).Forget();
        }

        #region Implementation of IConnection

        /// <summary>
        ///     Send data with channel specific settings. (NOOP atm until mirrorng links it)
        /// </summary>
        /// <param name="data">The data to be sent.</param>
        /// <param name="channel">The channel to send it on.</param>
        /// <returns></returns>
        public void Send(ArraySegment<byte> data, int channel)
        {
            if (CancelToken.IsCancellationRequested)
            {
                return;
            }

            if (!Client.IsSet || Client.State == PeerState.Uninitialized)
            {
                return;
            }

            if (channel > Config.Channels.Length)
            {
                Debug.LogWarning(
                    $"Ignorance: Attempted to send data on channel {channel} when we only have {Config.Channels.Length} channels defined");
                return;
            }

            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)Config.Channels[channel]);

            IgnoranceOutgoingMessage ignoranceOutgoingMessage = default;

            ignoranceOutgoingMessage.ChannelId = (byte)channel;
            ignoranceOutgoingMessage.Payload = payload;

            OutgoingQueuedData.Enqueue(ignoranceOutgoingMessage);

            if (Config.DebugEnabled)
            {
                Debug.Log(
                    $"Ignorance: Queuing up outgoing data packet: {BitConverter.ToString(data.Array)}");
            }
        }

        /// <summary>
        ///     reads a message from connection
        /// </summary>
        /// <param name="buffer">buffer where the message will be written</param>
        /// <returns>The channel where we got the message</returns>
        /// <remark> throws System.IO.EndOfStreamException if the connetion has been closed</remark>
        public async UniTask<int> ReceiveAsync(MemoryStream buffer)
        {
            try
            {
                while (!CancelToken.IsCancellationRequested)
                {
                    while (IncomingQueuedData.TryDequeue(out IgnoranceIncomingMessage ignoranceIncomingMessage))
                    {
                        buffer.SetLength(0);

                        if (Config.DebugEnabled)
                        {
                            Debug.Log(
                                $"Ignorance: Sending incoming data to mirror: {BitConverter.ToString(ignoranceIncomingMessage.Data)}");
                        }

                        await buffer.WriteAsync(ignoranceIncomingMessage.Data, 0, ignoranceIncomingMessage.Data.Length);

                        return ignoranceIncomingMessage.ChannelId;
                    }

                    await UniTask.Delay(1);
                }

                throw new EndOfStreamException();
            }
            catch (OperationCanceledException)
            {
                // Normal operation cancellation token has fired off. Let's ignore this.
                if (Config.DebugEnabled)
                {
                    Debug.Log(
                        "Ignorance: Cancellation token cancelled");
                }

                throw new EndOfStreamException();
            }
            catch (SocketException ex)
            {
                Debug.LogError($"Ignorance: this is normal other end could of closed connection. {ex}");
                throw new EndOfStreamException();
            }
        }

        /// <summary>
        ///     Disconnect this client.
        /// </summary>
        public void Disconnect()
        {
            CancelToken.Cancel();

            // Clean the queues.
            while (IncomingQueuedData.TryDequeue(out _))
            {
                // do nothing
            }

            while (OutgoingQueuedData.TryDequeue(out _))
            {
                // do nothing
            }

            if (Client.IsSet)
            {
                Client.DisconnectNow(0);
            }

            if (_serverConnection) return;

            _clientHost.Flush();
            _clientHost.Dispose();
        }

        /// <summary>
        ///     the address of endpoint we are connected to
        ///     Note this can be IPEndPoint or a custom implementation
        ///     of EndPoint, which depends on the transport
        /// </summary>
        /// <returns></returns>
        public EndPoint GetEndPointAddress()
        {
            return CancelToken.IsCancellationRequested
                ? null
                : new IPEndPoint(IPAddress.Parse(Config.ServerBindAddress), Client.Port);
        }

        #endregion

        /// <summary>
        ///     Process all incoming messages and queue them up for mirror.
        /// </summary>
        private async UniTaskVoid ProcessMessages()
        {
            // Only process messages if the client is valid.
            while (!CancelToken.IsCancellationRequested)
            {
                // Setup...
                uint nextStatsUpdate = 0;

                bool clientWasPolled = false;

                if (Library.Time >= nextStatsUpdate)
                {
                    _statistics.CurrentPing = Client.RoundTripTime;
                    _statistics.BytesReceived = Client.BytesReceived;
                    _statistics.BytesSent = Client.BytesSent;

                    _statistics.PacketsLost = Client.PacketsLost;
                    _statistics.PacketsSent = Client.PacketsSent;

                    // Library.Time is milliseconds, so we need to do some quick math.
                    nextStatsUpdate = Library.Time + (uint) (_pingUpdateInterval * 1000);
                }

                while (!clientWasPolled && !(_clientHost is null))
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
                                    var incomingIgnoranceMessage =
                                        new IgnoranceIncomingMessage
                                        {
                                            ChannelId = networkEvent.ChannelID,
                                            Data = new byte[networkEvent.Packet.Length]
                                        };

                                    networkEvent.Packet.CopyTo(incomingIgnoranceMessage.Data);

                                    IncomingQueuedData.Enqueue(incomingIgnoranceMessage);

                                    if (Config.DebugEnabled)
                                        Debug.Log(
                                            $"Ignorance: Queuing up incoming data packet: {BitConverter.ToString(incomingIgnoranceMessage.Data)}");
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
                    await UniTask.Delay(10);
                }

                while (OutgoingQueuedData.TryDequeue(out IgnoranceOutgoingMessage message))
                {
                    int returnCode = Client.Send(message.ChannelId, ref message.Payload);

                    if (returnCode == 0)
                    {
                        if (Config.DebugEnabled)
                            Debug.Log(
                                $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} OK");

                        continue;
                    }

                    if (Config.DebugEnabled)
                        Debug.Log(
                            $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} FAIL, code {returnCode}");

                }

                await UniTask.Delay(10);
            }
        }
    }
}
