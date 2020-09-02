#region Statements

using System;
using System.IO;
using System.Net;
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
        private bool _clientStarted;
        private readonly Host _clientHost;

        public ENetConnection(Peer client, Host host, Configuration config)
        {
            _client = client;
            _config = config;
            _clientHost = host;

            _clientStarted = true;
        }

        #region Disconnect

        public void Disconnect()
        {
            if (_client.IsSet) _client.DisconnectNow(0);
        }

        #endregion

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public EndPoint GetEndPointAddress()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="data"></param>
        /// <param name="channel"></param>
        /// <returns></returns>
        public Task SendAsync(ArraySegment<byte> data, int channel)
        {
            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[channel]);

            int returnCode = _client.SendAndReturnStatusCode((byte)channel, ref payload);

            return returnCode == 0 ? Task.CompletedTask : null;
        }

        #region Receiving

        /// <summary>
        /// 
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public async Task<bool> ReceiveAsync(MemoryStream buffer)
        {
            buffer.SetLength(0);

            try
            {
                bool clientWasPolled = false;

                // Only process messages if the client is valid.
                while (!clientWasPolled)
                {
                    if (_clientHost.CheckEvents(out Event networkEvent) <= 0)
                    {
                        if (_clientHost.Service(0, out networkEvent) <= 0) await Task.Delay(1);
                        clientWasPolled = true;
                    }

                    switch (networkEvent.Type)
                    {
                        case EventType.Connect:
                            // Client connected.
                            break;
                        case EventType.Timeout:
                        case EventType.Disconnect:
                            // Client disconnected.
                            Disconnect();
                            break;
                        case EventType.Receive:
                            // Client recieving some data.
                            // Debug.Log("Data");
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

                                    await buffer.WriteAsync(rentedBuffer, 0, networkEvent.Packet.Length);

                                    System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer, true);
                                }
                                catch (Exception e)
                                {
                                    Debug.LogError(
                                        $"Ignorance caught an exception while trying to copy data from the unmanaged (ENET) world to managed (Mono/IL2CPP) world. Please consider reporting this to the Ignorance developer on GitHub.\n" +
                                        $"Exception returned was: {e.Message}\n" +
                                        $"Debug details: {(_config.PacketCache == null ? "packet buffer was NULL" : $"{_config.PacketCache.Length} byte work buffer")}, {networkEvent.Packet.Length} byte(s) network packet length\n" +
                                        $"Stack Trace: {e.StackTrace}");
                                    networkEvent.Packet.Dispose();
                                    break;
                                }

                                networkEvent.Packet.Dispose();

                            }

                            break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return false;
        }

        #endregion

        #region Sending

        /// <summary>
        /// 
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public Task SendAsync(ArraySegment<byte> data)
        {
            Packet payload = default;
            payload.Create(data.Array, data.Offset, data.Count + data.Offset, (PacketFlags)_config.Channels[0]);

            int returnCode = _client.SendAndReturnStatusCode((byte)0, ref payload);

            return Task.CompletedTask;
        }

        #endregion
    }
}