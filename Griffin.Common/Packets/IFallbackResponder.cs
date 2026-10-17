using Griffin.Common.Networking;
using Griffin.Common.Protocol;

namespace Griffin.Common.Packets
{
    /// <summary>
    /// Last-chance answer for a client request that has no (or only a stub) handler. <see cref="PacketDispatcher"/> asks every
    /// registered responder, in registration order, before falling back to the empty-reply rules; the first one that
    /// returns true has fully handled the packet (and sent whatever the client expects).
    /// </summary>
    public interface IFallbackResponder
    {
        bool TryRespond(Connection connection, BasePacket packet);
    }
}
