using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Handlers
{
    public sealed class NrtPvpHandler(Griffin.Common.Table.ITableService tableService) : NrtPvpHandlerBase
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
            // The client builds its defence-team table only from nrt_pvp_defends; with none it stayed null and every panel that asks
            // for the PvP defence team (the AFK assistant's PvP row) threw a NullReferenceException each frame. Report the default map
            // (NrtpvpMapData map_type 1) as current, with an empty defence team.
            uint map = tableService.GetTable<NrtpvpMapData>().Where(m => m.MapType == 1).Select(m => m.Id).DefaultIfEmpty(1004u).First();
            connection.Send(new SC_NrtPvpInfo
            {
                NrtPvpDefends = { new PvpDefend { MapId = map } },
                NrtCurrMapId = map,
            });
        }
    }
}
