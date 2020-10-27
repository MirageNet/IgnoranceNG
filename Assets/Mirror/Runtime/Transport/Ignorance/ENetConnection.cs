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

namespace Mirror.ENet
{
    public class ENetConnection : IConnection
    {
        #region Fields

        private Peer _client;
        private readonly Configuration _config;
        private readonly Host _clientHost;
        private readonly ConcurrentQueue<IgnoranceIncomingMessage> _incomingQueuedData = new ConcurrentQueue<IgnoranceIncomingMessage>();
        private readonly ConcurrentQueue<IgnoranceOutgoingMessage> _outgoingQueuedData = new ConcurrentQueue<IgnoranceOutgoingMessage>();
        private readonly CancellationTokenSource _cancelToken = new CancellationTokenSource();
        private static volatile PeerStatistics _statistics = new PeerStatistics();
        private readonly int _pingUpdateInterval;

        #endregion

        /// <summary>
        ///     Initialize Constructor.
        /// </summary>
        /// <param name="client">The peer we are connecting with or to.</param>
        /// <param name="host">The host we are connecting with or to.</param>
        /// <param name="config">The configuration file to be used for all client connections.</param>
        public ENetConnection(Peer client, Host host, Configuration config)
        {
            _client = client;
            _config = config;
            _clientHost = host;
            _statistics = new PeerStatistics();
            _pingUpdateInterval = _config.StatisticsCalculationInterval;

            UniTask.Run(ProcessMessages).Forget();
        }

        /// <summary>
        ///     Process all incoming messages and queue them up for mirror.
        /// </summary>
        private async UniTaskVoid ProcessMessages()
        {
            // Setup...
            uint nextStatsUpdate = 0;

            // Only process messages if the client is valid.
            while (!_cancelToken.IsCancellationRequested)
            {
                bool clientWasPolled = false;

                if (Library.Time >= nextStatsUpdate)
                {
                    _statistics.CurrentPing = _client.RoundTripTime;
                    _statistics.BytesReceived = _client.BytesReceived;
                    _statistics.BytesSent = _client.BytesSent;

                    _statistics.PacketsLost = _client.PacketsLost;
                    _statistics.PacketsSent = _client.PacketsSent;

                    // Library.Time is milliseconds, so we need to do some quick math.
                    nextStatsUpdate = Library.Time + (uint)(_pingUpdateInterval * 1000);
                }

                while (!clientWasPolled)
                {
                    if (_clientHost.CheckEvents(out Event networkEvent) <= 0)
                    {
                        if (_clientHost.Service(_config.EnetPollTimeout, out networkEvent) <= 0) break;
                        clientWasPolled = true;
                    }

                    switch (networkEvent.Type)
                    {
                        case EventType.Timeout:
                        case EventType.Disconnect:

                            if (_config.DebugEnabled) Debug.Log($"Ignorance: Dead Peer. {networkEvent.Peer.ID}.");

                            Disconnect();

                            networkEvent.Packet.Dispose();

                            break;
                        case EventType.Receive:
                            // Client recieving some data.
                            if (_client.ID != networkEvent.Peer.ID)
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
                                    IgnoranceIncomingMessage incomingIgnoranceMessage = default;
                                    incomingIgnoranceMessage.ChannelId = networkEvent.ChannelID;
                                    incomingIgnoranceMessage.Data = new byte[networkEvent.Packet.Length];

                                    networkEvent.Packet.CopyTo(incomingIgnoranceMessage.Data);

                                    _incomingQueuedData.Enqueue(incomingIgnoranceMessage);

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
                }

                while (_outgoingQueuedData.TryDequeue(out IgnoranceOutgoingMessage message))
                {
                    int returnCode = _client.Send(message.ChannelId, ref message.Payload);

                    if (returnCode == 0)
                    {
                        if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} OK");

                        await UniTask.Delay(1);

                        continue;
                    }

                    if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} FAIL, code {returnCode}");

                    await UniTask.Delay(1);
                }

                await UniTask.Delay(1);
            }
        }

        /// <summary>
        ///     Disconnect client from server.
        /// </summary>
        public void Disconnect()
        {
            _cancelToken.Cancel();

            // Clean the queues.
            while (_incomingQueuedData.TryDequeue(out _))
            {
                // do nothing
            }

            while (_outgoingQueuedData.TryDequeue(out _))
            {
                // do nothing
            }

            if (_client.IsSet) _client.DisconnectNow(0);

            if(_clientHost == null || !_clientHost.IsSet) return;

            _clientHost.Flush();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public EndPoint GetEndPointAddress()
        {
            return _cancelToken.IsCancellationRequested
                ? null
                : new IPEndPoint(IPAddress.Parse(_config.ServerBindAddress), _client.Port);
        }

        /// <summary>
        ///     Send data with channel specific settings. (NOOP atm until mirrorng links it)
        /// </summary>
        /// <param name="data">The data to be sent.</param>
        /// <param name="channel">The channel to send it on.</param>
        /// <returns></returns>
        public UniTask SendAsync(ArraySegment<byte> data, int channel)
        {
            if (_cancelToken.IsCancellationRequested) return UniTask.CompletedTask;

            if (!_client.IsSet || _client.State == PeerState.Uninitialized) return UniTask.CompletedTask;

            if (channel > _config.Channels.Length)
            {
                Debug.LogWarning($"Ignorance: Attempted to send data on channel {channel} when we only have {_config.Channels.Length} channels defined");
                return UniTask.CompletedTask;
            }

            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[channel]);

            IgnoranceOutgoingMessage ignoranceOutgoingMessage = default;

            ignoranceOutgoingMessage.ChannelId = (byte) channel;
            ignoranceOutgoingMessage.Payload = payload;

            _outgoingQueuedData.Enqueue(ignoranceOutgoingMessage);

            if (_config.DebugEnabled)
                Debug.Log(
                    $"Ignorance: Queuing up outgoing data packet: {BitConverter.ToString(data.Array)}");

            return UniTask.CompletedTask;
        }

        /// <summary>
        ///     Process queued incoming data and pass it along to mirror.
        /// </summary>
        /// <param name="buffer">The memory stream buffer to write data to.</param>
        /// <returns></returns>
        public async UniTask<int> ReceiveAsync(MemoryStream buffer)
        {
            try
            {
                while (!_cancelToken.IsCancellationRequested)
                {
                    while (_incomingQueuedData.TryDequeue(out IgnoranceIncomingMessage ignoranceIncomingMessage))
                    {
                        buffer.SetLength(0);

                        if (_config.DebugEnabled)
                            Debug.Log(
                                $"Ignorance: Sending incoming data to mirror: {BitConverter.ToString(ignoranceIncomingMessage.Data)}");

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
                if (_config.DebugEnabled)
                    Debug.Log(
                        $"Ignorance: Cancellation token cancelled");

                throw new EndOfStreamException();
            }
            catch (SocketException ex)
            {
                Debug.LogError($"Ignorance: this is normal other end could of closed connection. {ex}");
                throw new EndOfStreamException();
            }
        }
    }
}
