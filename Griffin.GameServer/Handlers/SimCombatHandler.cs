using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class SimCombatHandler : SimCombatHandlerBase
    {
        public override void HandleSimCombatInfo(CS_SimCombatInfo request, Connection connection)
        {
            connection.Send(new SC_SimCombatInfo
            {
                Info = new SimCombatInfo { AdvancedMaxLevel = 0 },
            });
        }

        public override void HandleSimCombatMythicInfo(CS_SimCombatMythicInfo request, Connection connection)
        {
            connection.Send(
            new SC_SimCombatMythicInfo
            {
                StageInfo = { },
                GroupIds =
                { },
                NextTime = 0,
                CanRecive = false,
                Info = new EKOHFHNPMFN
                {
                    JMMDJGMFEIG = 0,
                    StageInfo = { },
                    ELECCHPPDOD = { },
                },
            }
            );
        }

        // No mythic hard run is recorded on this server (no battle result of that mode is kept), so the history is empty.
        public override void HandleSimCombatMythicHardHistory(CS_SimCombatMythicHardHistory request, Connection connection) =>
            connection.Send(new SC_SimCombatMythicHardHistory());

        public override void HandleSimCombatMythicHardInfo(CS_SimCombatMythicHardInfo request, Connection connection)
        {
            connection.Send(
            new SC_SimCombatMythicHardInfo
            {
                Current =
                {
                    new CBMLKOLAIJA.Types.HDBBHGABLCP
                    {
                        EPCCANNLACO = 26,
                        JOCEPCJGCDA = { },
                        PlanId = 180026,
                    },
                },
            }
            );
        }

        public override void HandleSimCombatResourceInfo(CS_SimCombatResourceInfo request, Connection connection)
        {
            connection.Send(
            new SC_SimCombatResourceInfo
            {
                SimCombatResource = { },
            }
            );
        }
    }
}
