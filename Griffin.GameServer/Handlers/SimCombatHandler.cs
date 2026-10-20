using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class SimCombatHandler(ISimCombatWeekService week) : SimCombatHandlerBase
    {
        /// <summary>Retreat from the attempt of the weekly mode: the squads and the score are dropped, the ticket is not given back (A-131).</summary>
        public override void HandleSimCombatWeekRetreat(CS_SimCombatWeekRetreat request, Connection connection)
        {
            if (connection.Account is null)
                return;

            bool boss = request.Status == SimCombatWeekly.Types.Status.Final;
            if (boss)
                week.AbandonBoss(connection.Account.Uid, request.KNPHEBDLFFP);
            else
                week.Abandon(connection.Account.Uid);
            connection.Send(new SC_SimCombatWeekRetreat
            {
                Status = request.Status,
                MCKEKMMKFFA = request.KNPHEBDLFFP,
                CEAKINDONJD = boss ? week.BossOf(connection.Account.Uid, request.KNPHEBDLFFP).Attempts : week.ViewOf(connection.Account.Uid).Attempts,
            });
        }

        public override void HandleSimCombatWeekQuit(CS_SimCombatWeekQuit request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (request.Status == SimCombatWeekly.Types.Status.Final)
                week.AbandonBoss(connection.Account.Uid, request.KNPHEBDLFFP);
            else
                week.Abandon(connection.Account.Uid);
            connection.Send(new SC_SimCombatWeekQuit { Status = request.Status, KNPHEBDLFFP = request.KNPHEBDLFFP });
        }

        /// <summary>Sweep of a level or boss that was cleared before (A-134).</summary>
        public override void HandleSimCombatWeekRaid(CS_SimCombatWeekRaid request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            if (week.Raid(connection.Account.Uid, request.Status, request.KNPHEBDLFFP, messages) is { } error)
            {
                connection.Refuse(MsgId.MsgCsSimCombatWeekRaid, error);
                return;
            }

            connection.SendAutoEncrypted(messages.ToArray());
        }

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
