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

#endregion

namespace Mirror.ENet
{
    public abstract class Common : IConnection
    {
        #region Class Specific

        public Common(Peer client, Configuration config)
        {
            Config = config;
            Client = client;
            CancelToken = new CancellationTokenSource();

            UniTask.Run(ProcessMessages).Forget();
        }

        protected abstract UniTaskVoid ProcessMessages();

        #endregion

        #region Fields

        internal Peer Client;

        internal readonly ConcurrentQueue<IgnoranceIncomingMessage> IncomingQueuedData =
            new ConcurrentQueue<IgnoranceIncomingMessage>();

        internal readonly ConcurrentQueue<IgnoranceOutgoingMessage> OutgoingQueuedData =
            new ConcurrentQueue<IgnoranceOutgoingMessage>();

        internal readonly Configuration Config;
        internal readonly CancellationTokenSource CancelToken;

        #endregion

        #region Implementation of IConnection

        /// <summary>
        ///     Send data with channel specific settings. (NOOP atm until mirrorng links it)
        /// </summary>
        /// <param name="data">The data to be sent.</param>
        /// <param name="channel">The channel to send it on.</param>
        /// <returns></returns>
        public UniTask SendAsync(ArraySegment<byte> data, int channel)
        {
            if (CancelToken.IsCancellationRequested)
            {
                return UniTask.CompletedTask;
            }

            if (!Client.IsSet || Client.State == PeerState.Uninitialized)
            {
                return UniTask.CompletedTask;
            }

            if (channel > Config.Channels.Length)
            {
                Debug.LogWarning(
                    $"Ignorance: Attempted to send data on channel {channel} when we only have {Config.Channels.Length} channels defined");
                return UniTask.CompletedTask;
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

            return UniTask.CompletedTask;
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
    }
}
