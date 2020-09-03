#region Statements

using System;
using UnityEngine;
using UnityEngine.Serialization;

#endregion

namespace Mirror.ENet
{
    [Serializable]
    public class Configuration
    {
        [Header("Channel Definitions")] 
        public IgnoranceChannelTypes[] Channels = new IgnoranceChannelTypes[2];

        public int CommunicationPort = 7777;

        [Header("Custom Peer and Timeout Settings")]
        public bool CustomMaxPeerLimit = false;

        public int CustomMaxPeers = 1000;
        public uint CustomTimeoutBaseTicks = 5000;
        public bool CustomTimeoutLimit = false;
        public uint CustomTimeoutMultiplier = 3;

        [Header("Debug Options")] public bool DebugEnabled = false;

        public int MaximumPeerCCU = 4095;

        [Header("Security")] [FormerlySerializedAs("MaxPacketSize")]
        public int MaxPacketSizeInKb = 16;

        [Header("Ping Calculation")]
        [Tooltip(
            "This value (in seconds) controls how often the client peer ping value will be retrieved from the ENET world. Note that too low values can actually harm performance due to excessive polling. " +
            "Keep it frequent, but not too frequent. 3 - 5 seconds should be OK. 0 to disable.")]
        public int PingCalculationInterval = 3;

        public string ServerBindAddress = "127.0.0.1";

        [Header("UDP Server and Client Settings")]
        public bool ServerBindAll = true;

        [NonSerialized] public byte[] PacketCache;
    }
}