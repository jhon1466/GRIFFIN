using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The weekly mode 要塞伯爵 (normal challenge, <see cref="SimCombatWeekService"/>). The retreat / quit / raid requests of the same mode live in <see cref="SimCombatHandler"/>.
    /// The custom-boss challenge (final) is not run: asking for it is refused with the "state invalid" text (A-131).
    /// </summary>
    public sealed class SimCombatWeekHandler(ISimCombatWeekService week) : SimCombatWeekHandlerBase
    {
        public override void HandleSimCombatWeekInfo(CS_SimCombatWeekInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            (ushort msgId, byte[] payload) = week.BuildInfo(connection.Account.Uid, messages);
            connection.SendRawPacketsWith([(msgId, payload)], messages.ToArray());
        }

        public override void HandleSimCombatWeekStart(CS_SimCombatWeekStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            IReadOnlyList<IReadOnlyList<uint>> squads = request.Ids.Select(ids => (IReadOnlyList<uint>)ids.Id.ToList()).Where(ids => ids.Count > 0).ToList();
            if (request.Status == SimCombatWeekly.Types.Status.Final)
            {
                // the boss challenges (战线挑战 tab): one attempt of the boss named in the request (A-133)
                if (week.StartBoss(connection.Account.Uid, request.KNPHEBDLFFP, squads, messages) is { } bossError)
                {
                    connection.Refuse(MsgId.MsgCsSimCombatWeekStart, bossError);
                    return;
                }

                connection.SendAutoEncrypted(messages.ToArray());
                return;
            }

            if (request.Status != SimCombatWeekly.Types.Status.StepB)
            {
                connection.Refuse(MsgId.MsgCsSimCombatWeekStart, HNINDAOJCKP.ActivityMiniGameStatusInvalid);
                return;
            }

            if (week.Start(connection.Account.Uid, squads, messages, out (ushort MsgId, byte[] Payload) reply) is { } error)
            {
                connection.Refuse(MsgId.MsgCsSimCombatWeekStart, error);
                return;
            }

            connection.SendRawPacketsWith([reply], messages.ToArray());
        }

        public override void HandleSimCombatWeekStartNextStorey(CS_SimCombatWeekStartNextStorey request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (week.StartNextStorey(connection.Account.Uid, out SC_SimCombatWeekStartNextStorey? reply) is { } error)
            {
                connection.Refuse(MsgId.MsgCsSimCombatWeekStartNextStorey, error);
                return;
            }

            connection.Send(reply!);
        }
    }
}
