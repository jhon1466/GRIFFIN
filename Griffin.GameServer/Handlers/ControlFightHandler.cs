using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class ControlFightHandler : ControlFightHandlerBase
    {
        public override void HandleGetControlFightInfo(CS_GetControlFightInfo request, Connection connection)
        {
                    connection.Send(new SC_GetControlFightInfo
                    {
                        AGOKHELMAPP = new GBELKJBMGBP
                        {
                            CurrId = 0,
                            CGALBDIKHKH = { },
                            IONMLJBJODF = 0,
                        },
                    });
        }


        // Control fight (raid boss) rules need the boss state of the season, which the server does not keep: the requests are confirmed.
        public override void HandleControlFightRaidIndex(CS_ControlFightRaidIndex request, Connection connection) =>
            connection.Send(new SC_ControlFightRaidIndex { GroupId = request.GroupId, RaidTime = request.Num });

        public override void HandleControlFightReattack(CS_ControlFightReattack request, Connection connection) =>
            connection.Send(new SC_ControlFightReattack { GroupId = request.GroupId });

        public override void HandleControlFightSettleStage(CS_ControlFightSettleStage request, Connection connection) =>
            connection.Send(new SC_ControlFightSettleStage { GroupId = request.GroupId, BPNFDHPFKHB = request.BPNFDHPFKHB });
    }
}
