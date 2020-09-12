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
        #region Fields

        private Peer _client;
        private readonly Configuration _config;
        private readonly Host _clientHost;
        private uint _nextPingCalculationTime = 0, _currentClientPing = 0;
        private readonly ConcurrentQueue<byte[]> _queuedData = new ConcurrentQueue<byte[]>();
        private readonly CancellationTokenSource _cancelToken = new CancellationTokenSource();

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

            _ = Task.Run(ProcessMessages, _cancelToken.Token);
            _ = Task.Run(PingUpdate, _cancelToken.Token);
        }

        /// <summary>
        ///     Update ping tracking.
        /// </summary>
        private void PingUpdate()
        {
            while(!_cancelToken.IsCancellationRequested)
            {
                // Is ping calculation enabled?
                if (_config.PingCalculationInterval <= 0)
                {
                    continue;
                }

                // Time to recalculate our ping?
                if (_nextPingCalculationTime < Library.Time)
                {
                    continue;
                }

                // If the peer is set, then poll it. Otherwise it might not be time to do that.
                if (_client.IsSet) _currentClientPing = _client.RoundTripTime;

                _nextPingCalculationTime = (uint)(Library.Time + (_config.PingCalculationInterval * 1000));
            }
        }

        /// <summary>
        ///     Process all incoming messages and queue them up for mirror.
        /// </summary>
        private void ProcessMessages()
        {
            // Only process messages if the client is valid.
            while (!_cancelToken.IsCancellationRequested)
            {
                bool clientWasPolled = false;

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

                            if (_config.DebugEnabled) Debug.Log($"Ignorance: Dead Peer. {networkEvent.Peer.ID}.");

                            Disconnect();

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
                                    Debug.Log(
                                        $"Ignorance: Packet too big to fit in buffer. {networkEvent.Packet.Length} packet bytes vs {_config.PacketCache.Length} cache bytes {networkEvent.Peer.ID}.");
                                networkEvent.Packet.Dispose();
                            }
                            else
                            {
                                // invoke on the client.
                                try
                                {
                                    byte[] rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(networkEvent.Packet.Length);
                                    networkEvent.Packet.CopyTo(rentedBuffer);

                                    _queuedData.Enqueue(rentedBuffer);

                                    if (_config.DebugEnabled)
                                        Debug.Log(
                                            $"Ignorance: Queuing up data packet: {BitConverter.ToString(rentedBuffer)}");
                                }
                                catch (Exception e)
                                {
                                    Debug.LogError(
                                        $"Ignorance caught an exception while trying to copy data from the unmanaged (ENET) world to managed (Mono/IL2CPP) world. Please consider reporting this to the Ignorance developer on GitHub.\n" +
                                        $"Exception returned was: {e.Message}\n" +
                                        $"Debug details: {(_config.PacketCache == null ? "packet buffer was NULL" : $"{_config.PacketCache.Length} byte work buffer")}, {networkEvent.Packet.Length} byte(s) network packet length\n" +
                                        $"Stack Trace: {e.StackTrace}");
                                }


                                networkEvent.Packet.Dispose();
                            }

                            break;
                    }
                }
            }
        }

        /// <summary>
        ///     Disconnect client from server.
        /// </summary>
        public void Disconnect()
        {
            _cancelToken.Cancel();

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
        public Task SendAsync(ArraySegment<byte> data, int channel)
        {
            if (_cancelToken.IsCancellationRequested) return null;

            if (!_client.IsSet || _client.State == PeerState.Uninitialized) return null;

            if (channel > _config.Channels.Length)
            {
                Debug.LogWarning($"Ignorance: Attempted to send data on channel {channel} when we only have {_config.Channels.Length} channels defined");
                return null;
            }

            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[channel]);

            int returnCode = _client.Send((byte)channel, ref payload);

            if (returnCode == 0)
            {
                if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {channel} OK");

                return Task.CompletedTask;
            }

            if (_config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {channel} FAIL, code {returnCode}");

            return null;
        }

        /// <summary>
        ///     Process queued incoming data and pass it along to mirror.
        /// </summary>
        /// <param name="buffer">The memory stream buffer to write data to.</param>
        /// <returns></returns>
        public async Task<bool> ReceiveAsync(MemoryStream buffer)
        {
            try
            {
                while (_queuedData.IsEmpty)
                {
                    if (_cancelToken.IsCancellationRequested) return false;

                    await Task.Delay(1);
                }

                _queuedData.TryDequeue(out byte[] data);

                buffer.SetLength(0);

                if (_config.DebugEnabled)
                    Debug.Log(
                        $"Ignorance: Sending data to mirror: {BitConverter.ToString(data)}");

                await buffer.WriteAsync(data, 0, data.Length);

                return true;
            }
            catch (OperationCanceledException)
            {
                // Normal operation cancellation token has fired off. Let's ignore this.
                return false;
            }
            catch(Exception ex)
            {
                Debug.LogError($"Ignorance: During processing of incoming data something went wrong. {ex}");
                return false;
            }
        }

        /// <summary>
        ///     Send data on the default channel 0.
        /// </summary>
        /// <param name="data">The data to send.</param>
        /// <returns></returns>
        public Task SendAsync(ArraySegment<byte> data)
        {
            if (_cancelToken.IsCancellationRequested) return null;

            if (!_client.IsSet || _client.State == PeerState.Uninitialized) return null;

            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[0]);

            int returnCode = _client.Send(0, ref payload);

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
