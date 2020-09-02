#region Statements

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ENet;
using UnityEngine;

#endregion

namespace Mirror.ENet
{
    public class IgnoranceClassic : Transport
    {
        #region Fields

        public Configuration Config = null;

        private IgnoranceServer _server;
        private ENetConnection _client;
        private readonly string Version = "1.3.8";
        private bool ENETInitialized;

        #endregion

        #region Unity Methods

        // Sanity checks.
        private void OnValidate()
        {
            if (Config.Channels != null && Config.Channels.Length >= 2)
            {
                // Check to make sure that Channel 0 and 1 are correct.
                if (Config.Channels[0] != IgnoranceChannelTypes.Reliable) Config.Channels[0] = IgnoranceChannelTypes.Reliable;
                if (Config.Channels[1] != IgnoranceChannelTypes.Unreliable) Config.Channels[1] = IgnoranceChannelTypes.Unreliable;
            }
            else
            {
                Config.Channels = new IgnoranceChannelTypes[2]
                {
                    IgnoranceChannelTypes.Reliable,
                    IgnoranceChannelTypes.Unreliable
                };
            }
        }

        #endregion

        private bool InitializeENET()
        {
            Config.PacketCache = new byte[Config.MaxPacketSizeInKb * 1024];

            if (Config.DebugEnabled)
                Debug.Log($"Initialized new packet cache, {Config.MaxPacketSizeInKb * 1024} bytes capacity.");

            return Library.Initialize();
        }

        #region Overrides of Transport

        public override Task ListenAsync()
        {
            if (!ENETInitialized)
            {
                if (InitializeENET())
                {
                    Debug.Log("Ignorance successfully initialized ENET.");
                    ENETInitialized = true;
                }
                else
                {
                    Debug.LogError("Ignorance failed to initialize ENET! Cannot continue.");
                    return null;
                }
            }

            _server = new IgnoranceServer(Config);

            // start server up and listen for connections
            return _server.Start();
        }

        public override void Disconnect()
        {
            Config.PacketCache = new byte[Config.MaxPacketSizeInKb * 1024];

            _server?.Shutdown();
        }

        public override Task<IConnection> ConnectAsync(Uri uri)
        {
            if (!ENETInitialized)
            {
                if (InitializeENET())
                {
                    Debug.Log($"Ignorance successfully initialized ENET.");
                    ENETInitialized = true;
                }
                else
                {
                    Debug.LogError($"Ignorance failed to initialize ENET! Cannot continue.");
                    return null;
                }
            }

            if (Config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: ClientConnect({uri.Host})");

            if (Config.Channels.Length > 255)
            {
                Debug.LogError($"Ignorance: Too many channels. Channel limit is 255, you have {Config.Channels.Length}. This would probably crash ENET. Aborting connection.");
                return null;
            }

            if (Config.CommunicationPort < ushort.MinValue || Config.CommunicationPort > ushort.MaxValue)
            {
                Debug.LogError($"Ignorance: Bad communication port number. You need to set it between port 0 and 65535. Aborting connection.");
                return null;
            }

            var host = new Host();
            host.Create(null, 1, Config.Channels.Length, 0, 0, Config.PacketCache.Length);

            if (Config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Created ENET Host object");

            var address = new Address();
            address.SetHost(uri.Host);

            address.Port = (ushort)Config.CommunicationPort;

            var peer = host.Connect(address, Config.Channels.Length);

            if (Config.CustomTimeoutLimit)
                peer.Timeout(Library.throttleScale, Config.CustomTimeoutBaseTicks,
                    Config.CustomTimeoutBaseTicks * Config.CustomTimeoutMultiplier);

            if (Config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Client has been started!");

            var client = new ENetConnection(peer, host, Config);

            return Task.FromResult<IConnection>(client);
        }

        public override async Task<IConnection> AcceptAsync()
        {
            // Never attempt process anything if we're not initialized
            if (!ENETInitialized) return null;

            try
            {
                while (_server != null && (bool) _server?.ServerStarted)
                {
                    var client = await _server.AcceptConnections();

                    if (client != null)
                    {
                        return client;
                    }

                    await Task.Delay(1);
                }

                return null;
            }
            catch (ObjectDisposedException)
            {
                // expected,  the server was closed
                return null;
            }
        }

        public override IEnumerable<Uri> ServerUri()
        {
            {
                var builder = new UriBuilder
                {
                    Scheme = "enet",
                    Host = Config.ServerBindAddress,
                    Port = Config.CommunicationPort
                };
                return new[] {builder.Uri};
            }
        }

        public override IEnumerable<string> Scheme => new[] {"enet"};

        public override bool Supported => Application.platform != RuntimePlatform.WebGLPlayer;

        #endregion
    }
}