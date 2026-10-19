using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// 遗珍回收 "Rouge 1" (plans 600002 and 600003); the logic is in <see cref="RougeService"/>. Plan 600004 is the other system (<c>RougeGame*</c>).
    /// </summary>
    public sealed class RougeHandler(IRougeService rouge) : RougeHandlerBase
    {
        public override void HandleRougeAllPlans(CS_RougeAllPlans request, Connection connection)
        {
            connection.Send(new SC_RougeAllPlans
            {
                Plans =
                {
                    [600004] = true,
                    [600003] = true,
                    [600002] = true,
                },
            });
        }

        public override void HandleGetRougeInfo(CS_GetRougeInfo request, Connection connection)
        {
            if (connection.Account is null || !rouge.IsPlan(request.PlanId))
                return;

            connection.Send(new SC_GetRougeInfo { Info = rouge.BuildInfo(connection.Account.Uid, request.PlanId) });
        }

        public override void HandleGetRougeMapInfo(CS_GetRougeMapInfo request, Connection connection)
        {
            if (connection.Account is null || !rouge.IsPlan(request.PlanId))
                return;

            connection.Send(new SC_GetRougeMapInfo { Map = rouge.GetMap(connection.Account.Uid, request.PlanId), PlanId = request.PlanId });
        }

        public override void HandleRougePrepare(CS_RougePrepare request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (rouge.Prepare(connection.Account.Uid, request) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougePrepare, error);
                return;
            }

            connection.Send(new SC_RougePrepare { Info = rouge.BuildInfo(connection.Account.Uid, request.PlanId) });
        }

        public override void HandleRougeCancelPrepare(CS_RougeCancelPrepare request, Connection connection)
        {
            if (connection.Account is null || !rouge.IsPlan(request.PlanId))
                return;

            rouge.CancelPrepare(connection.Account.Uid, request.PlanId);
            connection.Send(new SC_RougeCancelPrepare { Info = rouge.BuildInfo(connection.Account.Uid, request.PlanId) });
        }

        public override void HandleRougeStart(CS_RougeStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var extra = new List<Google.Protobuf.IMessage>();
            if (rouge.Start(connection.Account.Uid, request.PlanId, out PLPBPIBDGNB? map, extra) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeStart, error);
                return;
            }

            extra.Insert(0, new SC_RougeStart { Map = map, PlanId = request.PlanId });
            connection.SendAutoEncrypted(extra.ToArray());
        }

        public override void HandleRougePackOp(CS_RougePackOp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            if (rouge.PackOp(connection.Account.Uid, request.PlanId, request.CJJJLMCADGM, request.IHLNPPPJELG, (uint)Math.Max(0, request.BEKLEGCIKCI), messages) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougePackOp, error);
                return;
            }

            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleRougeTakeGradeReward(CS_RougeTakeGradeReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            if (rouge.TakeGradeReward(connection.Account.Uid, request.PlanId, request.MMADAMIMGCM, messages, out List<uint> taken) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeTakeGradeReward, error);
                return;
            }

            messages.Insert(0, new SC_RougeTakeGradeReward { EJCFAFMIDBH = { taken }, PlanId = request.PlanId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleRougeUnlockTalent(CS_RougeUnlockTalent request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            if (rouge.UnlockTalent(connection.Account.Uid, request.PlanId, request.TalentId, messages, out List<uint> unlocked) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeUnlockTalent, error);
                return;
            }

            messages.Insert(0, new SC_RougeUnlockTalent { MMIKNJIBMBA = { unlocked }, PlanId = request.PlanId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleRougeFinish(CS_RougeFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var extra = new List<Google.Protobuf.IMessage>();
            if (rouge.Finish(connection.Account.Uid, request.PlanId, request.Reason, out SC_RougeFinish? result, extra) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeFinish, error);
                return;
            }

            extra.Insert(0, result!);
            connection.SendAutoEncrypted(extra.ToArray());
        }

        public override void HandleRougeSeasonInfo(CS_RougeSeasonInfo request, Connection connection)
        {
            connection.Send(new SC_RougeSeasonInfo
            {
                PlanId = 600004,
                KCCBNBAGLII = new BKJNOBAKFDO
                {
                    SeasonId = 4,
                    LLCCGNFALPF = { },
                    HFKBNKKKCAP = { },
                    PJENNLFCFOI = { },
                    CMALCHFEBMH = { },
                    BNBIKIDMMMC = 0,
                    MOIOMJBKIAJ = 0,
                    Level = 0,
                    BJAGNIJOBIL = 0,
                    IANJJGOOLBD = 0,
                    OFKLPJINEGI = false,
                },
            });
        }
    }

    /// <summary>The node requests of the 遗珍回收 map (preview, open, interact); the logic is in <see cref="RougeService"/>.</summary>
    public sealed class RougeNodeHandler(IRougeService rouge) : RougeNodeHandlerBase
    {
        public override void HandleRougeNodePreview(CS_RougeNodePreview request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (rouge.PreviewNode(connection.Account.Uid, request.PlanId, request.Id, out ODFMONHCBIO? node) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeNodePreview, error);
                return;
            }

            connection.Send(new SC_RougeNodePreview { GCMADDJNGMI = node, PlanId = request.PlanId });
        }

        public override void HandleRougeNodeOpen(CS_RougeNodeOpen request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (rouge.OpenNode(connection.Account.Uid, request.PlanId, request.Id, out ODFMONHCBIO? node, out PJDHPAHKGJL? gunPack) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeNodeOpen, error);
                return;
            }

            var replies = new List<Google.Protobuf.IMessage> { new SC_RougeNodeOpen { GCMADDJNGMI = node, PlanId = request.PlanId } };
            if (gunPack is not null)
                replies.Add(new SC_RougePackOp { HIOLNHOOLOE = gunPack, PlanId = request.PlanId });
            connection.SendAutoEncrypted(replies.ToArray());
        }

        public override void HandleRougeNodeInteract(CS_RougeNodeInteract request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var extra = new List<Google.Protobuf.IMessage>();
            if (rouge.InteractNode(connection.Account.Uid, request.PlanId, request.Id, request.LJJEFHEDHLB, request.KBJHHBCEPLB, out ODFMONHCBIO? node, extra) is { } error)
            {
                connection.RefuseRouge(MsgId.MsgCsRougeNodeInteract, error);
                return;
            }

            extra.Insert(0, new SC_RougeNodeInteract { GCMADDJNGMI = node, PlanId = request.PlanId });
            connection.SendAutoEncrypted(extra.ToArray());
        }
    }
}
