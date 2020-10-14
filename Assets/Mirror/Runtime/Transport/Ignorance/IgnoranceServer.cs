#region Statements

using System.Net;
using Cysharp.Threading.Tasks;
using ENet;
using UnityEngine;
using Event = ENet.Event;
using EventType = ENet.EventType;

#endregion

namespace Mirror.ENet
{
    public class IgnoranceServer
    {
        #region Fields

        private readonly Configuration _config;

        private Host _enetHost = new Host();
        private Address _enetAddress;
        public bool ServerStarted;

        #endregion

        /// <summary>
        ///     Initialize constructor.
        /// </summary>
        /// <param name="config"></param>
        public IgnoranceServer(Configuration config)
        {
            _config = config;
        }

        /// <summary>
        ///     Easy way to check if our host is not null and has been set.
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        private static bool IsValid(Host host) => host != null && host.IsSet;

        /// <summary>
        ///     Processes and accepts new incoming connections.
        /// </summary>
        /// <returns></returns>
        public async UniTask<ENetConnection> AcceptConnections()
        {
            // Never attempt to process anything if the server is not valid.
            if (!IsValid(_enetHost)) return null;

            // Never attempt to process anything if the server is not active.
            if (!ServerStarted) return null;

            bool serverWasPolled = false;

            if (_enetHost.CheckEvents(out Event networkEvent) <= 0)
            {
                if (_enetHost.Service(0, out networkEvent) <= 0) return null;

                serverWasPolled = true;
            }

            if (!serverWasPolled) return null;

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

                    var client = new ENetConnection(networkEvent.Peer, _enetHost, _config);

                    return await UniTask.FromResult(client);
            }

            return null;
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

            if (IsValid(_enetHost))
            {
                _enetHost.Dispose();
            }

            ServerStarted = false;
        }

        /// <summary>
        ///     Start up the server and initialize things
        /// </summary>
        /// <returns></returns>
        public UniTask Start()
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

            return UniTask.CompletedTask;
        }
    }
}
