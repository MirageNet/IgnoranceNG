#region Statements

using ENet;

#endregion

namespace Mirage.ENet
{
    public struct IgnoranceIncomingMessage
    {
        public byte ChannelId;
        public byte[] Data;
    }

    public struct IgnoranceOutgoingMessage
    {
        public byte ChannelId;
        public Packet Payload;
    }
}
