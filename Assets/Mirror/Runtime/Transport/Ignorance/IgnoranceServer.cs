#region Statements

using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using ENet;
using Event = ENet.Event;
using UnityEngine;
using EventType = ENet.EventType;

#endregion

namespace Mirror.ENet
{
    public class IgnoranceServer
    {
        #region Fields

        private readonly Configuration _config;

        private Host ENETHost = new Host();
        private Address ENETAddress;
        private Dictionary<int, Peer> ConnectionIDToPeers = new Dictionary<int, Peer>();
        private Dictionary<Peer, int> PeersToConnectionIDs = new Dictionary<Peer, int>();
        public bool ServerStarted;
        private static int NextConnectionID = 1;

        #endregion

        public IgnoranceServer(Configuration config)
        {
            _config = config;
        }

        private bool IsValid(Host host)
        {
            return host != null && host.IsSet;
        }

        /// <summary>
        ///     Processes and accepts new incoming connections.
        /// </summary>
        /// <returns></returns>
        public async Task<ENetConnection> AcceptConnections()
        {
            // Never attempt to process anything if the server is not valid.
            if (!IsValid(ENETHost)) return null;

            // Never attempt to process anything if the server is not active.
            if (!ServerStarted) return null;

            bool serverWasPolled = false;

            Event networkEvent;

            if (ENETHost.CheckEvents(out networkEvent) <= 0 && !serverWasPolled)
            {
                if (ENETHost.Service(0, out networkEvent) > 0)
                    serverWasPolled = true;
            }

            if (!serverWasPolled) return null;

            switch (networkEvent.Type)
            {
                case EventType.Connect:

                    int newConnectionID = NextConnectionID;

                    // A client connected to the server. Assign a new ID to them.
                    if (_config.DebugEnabled)
                    {
                        Debug.Log(
                            $"Ignorance: New connection from {networkEvent.Peer.IP}:{networkEvent.Peer.Port}");
                        Debug.Log(
                            $"Ignorance: Map {networkEvent.Peer.IP}:{networkEvent.Peer.Port} (ENET Peer {networkEvent.Peer.ID}) => Mirror World Connection {newConnectionID}");
                    }

                    if (_config.CustomTimeoutLimit)
                        networkEvent.Peer.Timeout(Library.throttleScale, _config.CustomTimeoutBaseTicks,
                            _config.CustomTimeoutBaseTicks * _config.CustomTimeoutMultiplier);

                    // Map them into our dictonaries.
                    PeersToConnectionIDs.Add(networkEvent.Peer, newConnectionID);
                    ConnectionIDToPeers.Add(newConnectionID, networkEvent.Peer);

                    NextConnectionID++;

                    break;
            }

            return await Task.FromResult(new ENetConnection(networkEvent.Peer, ENETHost, _config));
        }

        public void Shutdown()
        {
            if (_config.DebugEnabled)
            {
                Debug.Log("[DEBUGGING MODE] Ignorance: ServerStop()");
                Debug.Log("[DEBUGGING MODE] Ignorance: Cleaning the packet cache...");
            }

            if (_config.DebugEnabled) Debug.Log("Ignorance: Cleaning up lookup dictonaries");

            ConnectionIDToPeers.Clear();
            PeersToConnectionIDs.Clear();

            if (IsValid(ENETHost))
            {
                ENETHost.Dispose();
            }

            ServerStarted = false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public Task Start()
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
                    ENETAddress.SetIP(_config.ServerBindAddress);
                }
                else
                {
                    // Might be a hostname.
                    if (_config.DebugEnabled)
                        Debug.Log("Ignorance: Doesn't look like a valid IP address, assuming it's a hostname?");
                    ENETAddress.SetHost(_config.ServerBindAddress);
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

            ENETAddress.Port = (ushort) _config.CommunicationPort;
            if (ENETHost == null || !ENETHost.IsSet) ENETHost = new Host();

            // Go go go! Clear those corners!
            ENETHost.Create(ENETAddress, _config.CustomMaxPeerLimit ? _config.CustomMaxPeers : (int) Library.maxPeers,
                _config.Channels.Length, 0, 0);

            if (_config.DebugEnabled)
                Debug.Log(
                    "[DEBUGGING MODE] Ignorance: Server should be created now... If Ignorance immediately crashes after this line, please file a bug report on the GitHub.");

            ServerStarted = true;

            return Task.CompletedTask;
        }
    }
}