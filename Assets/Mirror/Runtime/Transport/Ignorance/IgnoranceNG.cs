#region Statements

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ENet;
using UnityEngine;

#endregion

namespace Mirror.ENet
{
    public class IgnoranceNG : Transport
    {
        #region Fields

        public Configuration Config = new Configuration();

        private IgnoranceServer _server;
        private ENetConnection _client;
        private bool _enetInitialized;

        #endregion

        #region Unity Methods

        private void LateUpdate()
        {
            _client?.Update();
        }

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

        #region Class Specific

        /// <summary>
        ///     Startup Enet library and initialize things.
        /// </summary>
        /// <returns></returns>
        private bool InitializeEnet()
        {
            Config.PacketCache = new byte[Config.MaxPacketSizeInKb * 1024];

            if (Config.DebugEnabled)
                Debug.Log($"Initialized new packet cache, {Config.MaxPacketSizeInKb * 1024} bytes capacity.");

            return Library.Initialize();
        }

        #endregion

        #region Overrides of Transport

        #region Server Stuff

        /// <summary>
        ///     Easy method to get server's uri connect link.
        /// </summary>
        /// <returns>Returns back a new uri for connecting to server.</returns>
        public override IEnumerable<Uri> ServerUri()
        {
            {
                var builder = new UriBuilder
                {
                    Scheme = "enet",
                    Host = Config.ServerBindAddress,
                    Port = Config.CommunicationPort
                };
                return new[] { builder.Uri };
            }
        }

        /// <summary>
        ///     Server accepts new incoming connections.
        /// </summary>
        /// <returns>Returns back a new <see cref="ENetConnection"/> back to mirror.</returns>
        public override async Task<IConnection> AcceptAsync()
        {
            // Never attempt process anything if we're not initialized
            if (!_enetInitialized) return null;

            try
            {
                while (_server != null && (bool)_server?.ServerStarted)
                {
                    ENetConnection client = await _server.AcceptConnections();

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

        /// <summary>
        ///     Start listening for incoming connection attempts.
        /// </summary>
        /// <returns>Returns completed if started up correctly.</returns>
        public override Task ListenAsync()
        {
            if (!_enetInitialized)
            {
                if (InitializeEnet())
                {
                    Debug.Log("Ignorance successfully initialized ENET.");
                    _enetInitialized = true;
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

        #endregion

        /// <summary>
        ///     Shutdown the transport and disconnect server and clients.
        /// </summary>
        public override void Disconnect()
        {
            Config.PacketCache = new byte[Config.MaxPacketSizeInKb * 1024];

            _enetInitialized = false;
            Library.Deinitialize();

            _server?.Shutdown();
            _client?.Disconnect();
        }

        /// <summary>
        ///     Client connect to a server.
        /// </summary>
        /// <param name="uri">The uri we want to connect to.</param>
        /// <returns></returns>
        public override Task<IConnection> ConnectAsync(Uri uri)
        {
            if (!_enetInitialized)
            {
                if (InitializeEnet())
                {
                    Debug.Log($"Ignorance successfully initialized ENET.");
                    _enetInitialized = true;
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
                Debug.LogError(
                    $"Ignorance: Too many channels. Channel limit is 255, you have {Config.Channels.Length}. This would probably crash ENET. Aborting connection.");
                return null;
            }

            if (Config.CommunicationPort < ushort.MinValue || Config.CommunicationPort > ushort.MaxValue)
            {
                Debug.LogError(
                    $"Ignorance: Bad communication port number. You need to set it between port 0 and 65535. Aborting connection.");
                return null;
            }

            var host = new Host();
            host.Create(null, 1, Config.Channels.Length, 0, 0, Config.PacketCache.Length);

            if (Config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Created ENET Host object");

            var address = new Address();
            address.SetHost(uri.Host);

            address.Port = (ushort) Config.CommunicationPort;

            Peer peer = host.Connect(address, Config.Channels.Length);

            if (Config.CustomTimeoutLimit)
                peer.Timeout(Library.throttleScale, Config.CustomTimeoutBaseTicks,
                    Config.CustomTimeoutBaseTicks * Config.CustomTimeoutMultiplier);

            if (Config.DebugEnabled) Debug.Log($"[DEBUGGING MODE] Ignorance: Client has been started!");

            _client = new ENetConnection(peer, host, Config);

            return Task.FromResult<IConnection>(_client);
        }

        /// <summary>
        ///     The type of scheme to be used in our uri
        /// </summary>
        public override IEnumerable<string> Scheme => new[] {"enet"};

        /// <summary>
        ///     What platform's this transport supports.
        /// </summary>
        public override bool Supported => Application.platform != RuntimePlatform.WebGLPlayer;

        #endregion
    }
}
