using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Serilog;

namespace Griffin.GameServer.Extensions
{
    /// <summary>
    /// Refusing a request the way the official server does: <c>SC_ReportError</c> carries the id of the request protocol and an
    /// error number of the client's error table (<c>HNINDAOJCKP</c>), and the client shows the matching hint instead of
    /// waiting for a reply that never comes. Only explicit refusals of user actions use this; unanswered background requests do not.
    /// </summary>
    public static class ConnectionErrors
    {
        public static void Refuse(this Connection connection, MsgId request, HNINDAOJCKP error)
        {
            Log.Information("Refusing {Request} with error {Error} ({Code}).", request, error, (int)error);
            connection.SendAutoEncrypted(new SC_ReportError { ProtocolId = (uint)(int)request, ErrorNo = (int)error });
        }
    }
}
