#region Statements

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ENet;
using UnityEngine;
using Event = ENet.Event;
using EventType = ENet.EventType;

#endregion

namespace Mirror.ENet
{
    public class ENetConnection : IChannelConnection
    {
        private Peer _client;
        private readonly Configuration _config;
        private readonly Host _clientHost;
        private uint NextPingCalculationTime = 0, CurrentClientPing = 0;
        private readonly ConcurrentQueue<ArraySegment<byte>> _queuedData = new ConcurrentQueue<ArraySegment<byte>>();
        private readonly CancellationTokenSource _cancelToken = new CancellationTokenSource();

        public ENetConnection(Peer client, Host host, Configuration config)
        {
            _client = client;
            _config = config;
            _clientHost = host;
        }

        public void Update()
        {
            // Is ping calculation enabled?
            if (_config.PingCalculationInterval > 0)
            {
                // Time to recalculate our ping?
                if (NextPingCalculationTime >= Library.Time)
                {
                    // If the peer is set, then poll it. Otherwise it might not be time to do that.
                    if (_client.IsSet) CurrentClientPing = _client.RoundTripTime;
                    NextPingCalculationTime = (uint)(Library.Time + (_config.PingCalculationInterval * 1000));
                }
            }
        }

        private void ProcessMessages()
        {
            bool clientWasPolled = false;

            // Only process messages if the client is valid.
            while (!clientWasPolled)
            {
                if (_clientHost.CheckEvents(out Event networkEvent) <= 0)
                {
                    if (_clientHost.Service(0, out networkEvent) <= 0) break;
                    clientWasPolled = true;
                }

                switch (networkEvent.Type)
                {
                    case EventType.Connect:
                        break;
                    case EventType.Timeout:
                    case EventType.Disconnect:
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

                        if (networkEvent.Packet.Length > _config.PacketCache.Length)
                        {
                            if (_config.DebugEnabled)
                                Debug.Log(
                                    $"Ignorance: Packet too big to fit in buffer. {networkEvent.Packet.Length} packet bytes vs {_config.PacketCache.Length} cache bytes {networkEvent.Peer.ID}.");
                            networkEvent.Packet.Dispose();
                        }
                        else
                        {
                            // invoke on the client.
                            try
                            {
                                byte[] rentedBuffer =
                                    System.Buffers.ArrayPool<byte>.Shared.Rent(networkEvent.Packet.Length);

                                networkEvent.Packet.CopyTo(rentedBuffer);

                                var msg = new ArraySegment<byte>(rentedBuffer, 0, networkEvent.Packet.Length);

                                _queuedData.Enqueue(msg);

                                if (_config.DebugEnabled)
                                    Debug.Log(
                                        $"Ignorance: Queuing up data packet: {BitConverter.ToString(msg.Array)}");

                                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer, true);

                                networkEvent.Packet.Dispose();
                            }
                            catch (Exception e)
                            {
                                Debug.LogError(
                                    $"Ignorance caught an exception while trying to copy data from the unmanaged (ENET) world to managed (Mono/IL2CPP) world. Please consider reporting this to the Ignorance developer on GitHub.\n" +
                                    $"Exception returned was: {e.Message}\n" +
                                    $"Debug details: {(_config.PacketCache == null ? "packet buffer was NULL" : $"{_config.PacketCache.Length} byte work buffer")}, {networkEvent.Packet.Length} byte(s) network packet length\n" +
                                    $"Stack Trace: {e.StackTrace}");
                                networkEvent.Packet.Dispose();
                            }
                        }

                        break;
                }
            }
        }

        public void Disconnect()
        {
            _cancelToken.Cancel();

            if (_client.IsSet) _client.DisconnectNow(0);

            if(_clientHost == null || !_clientHost.IsSet) return;

            _clientHost.Flush();
            _clientHost.Dispose();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public EndPoint GetEndPointAddress()
        {
            return new IPEndPoint(IPAddress.Parse(_config.ServerBindAddress), _client.Port);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="data"></param>
        /// <param name="channel"></param>
        /// <returns></returns>
        public Task SendAsync(ArraySegment<byte> data, int channel)
        {
            if (!_client.IsSet || _client.State != PeerState.Connected) return null;

            if (channel > _config.Channels.Length)
            {
                Debug.LogWarning($"Ignorance: Attempted to send data on channel {channel} when we only have {_config.Channels.Length} channels defined");
                return null;
            }

            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[channel]);

            int returnCode = _client.SendAndReturnStatusCode((byte)channel, ref payload);

            if (returnCode == 0)
            {
                if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {channel} OK");

                return Task.CompletedTask;
            }

            if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {channel} FAIL, code {returnCode}");

            return null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public async Task<bool> ReceiveAsync(MemoryStream buffer)
        {
            try
            {
                while (_queuedData.IsEmpty)
                {
                    ProcessMessages();

                    await Task.Delay(1);
                }

                if (_cancelToken.IsCancellationRequested) return false;

                _queuedData.TryDequeue(out var data);

                buffer.SetLength(0);

                await buffer.WriteAsync(data.Array, 0, data.Count);

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public Task SendAsync(ArraySegment<byte> data)
        {
            if (!_client.IsSet || _client.State != PeerState.Connected) return null;

            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[0]);

            int returnCode = _client.SendAndReturnStatusCode(0, ref payload);

            if (returnCode == 0)
            {
                if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {0} OK");
                return Task.CompletedTask;
            }

            if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {0} FAIL, code {returnCode}");

            return null;
        }
    }
}