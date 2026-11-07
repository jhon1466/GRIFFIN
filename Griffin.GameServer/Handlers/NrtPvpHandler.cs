using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class NrtPvpHandler : NrtPvpHandlerBase
    {
        /// <summary>
        /// The AFK assistant panel asks for the daily opponent refresh when it opens; answering with "PvP closed" made it show an error
        /// dialog. Reply that no refresh happened (the empty PvP state has no opponents).
        /// </summary>
        public override void HandleNrtPvpOpponentDailyRefresh(CS_NrtPvpOpponentDailyRefresh request, Connection connection) =>
            connection.Send(new SC_NrtPvpOpponentDailyRefresh { PEPGBOMKNCN = false });

        public override void HandleNrtPvpInfo(CS_NrtPvpInfo request, Connection connection)
        {
            // This local single-player server has no PvP matchmaking pool. Return a
            // correctly typed empty state instead of leaving the login request hanging.
            connection.Send(new SC_NrtPvpInfo());
        }
    }
}
