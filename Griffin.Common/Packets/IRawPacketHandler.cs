using Griffin.Common.Networking;

namespace Griffin.Common.Packets
{
    /// <summary>
    /// Handles packets from their raw bytes, before any typed parsing: for protocols whose checked-in proto types do not describe the wire format
    /// (the Darkov room, A-145). A handler that returns true has fully answered the packet.
    /// </summary>
    public interface IRawPacketHandler
    {
        bool TryHandle(Connection connection, ushort rawMsgId, byte[] payload);
    }
}
