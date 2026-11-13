using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Jigsaw mini game of the events (module 20006, theme 601 -> pic group 101), as in the client data manager (client 4.0.5142, CHMFAMNHDPM):
    /// the config id is a JigsawPicByPicGroupData row whose column 2 lists the pictures (JigsawPicData: 1 id, 3 order, 4 minutes after the
    /// PlanData start of the module before the picture opens, 5 reward item -> count). SC_JigsawInfo carries one entry per completed picture
    /// (key = picture id, value = reward claimed); a picture missing from the map is not completed. The puzzle itself is played on the client,
    /// which sends CompleteOnePic when every piece is placed and ClaimReward afterwards.
    /// </summary>
    public sealed class JigsawHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : JigsawHandlerBase
    {
        private static string Key(uint theme) => $"jigsaw.{theme}";

        /// <summary>Completed pictures of the theme -> reward claimed.</summary>
        private Dictionary<uint, bool> Load(uint uid, uint theme) => accountState.Get<Dictionary<uint, bool>>(uid, Key(theme)) ?? [];

        /// <summary>The JigsawPicData row of <paramref name="pic"/> if it belongs to the pic group of the theme.</summary>
        private Google.Protobuf.IMessage? Picture(uint theme, uint pic)
        {
            uint config = ThemeModules.Config(tableService, theme, ThemeModules.Jigsaw);
            var group = tableService.GetTable<JigsawPicByPicGroupData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
            if (group is null || !TableFields.List(group, 2).Contains(pic))
                return null;
            return tableService.GetTable<JigsawPicData>().FirstOrDefault(r => TableFields.U(r, 1) == pic);
        }

        /// <summary>Unix start of the PlanData window of the module in this theme (ActivityModuleData column 10), or 0.</summary>
        private long PlanStart(uint theme)
        {
            var entrance = tableService.GetTable<ActivityEntranceData>().FirstOrDefault(r => TableFields.U(r, 1) == theme);
            if (entrance is null)
                return 0;
            uint moduleRow = TableFields.U(entrance, 2);
            var module = tableService.GetTable<ActivityModuleData>().FirstOrDefault(r => TableFields.U(r, 1) == moduleRow);
            if (module is null)
                return 0;
            uint plan = TableFields.Map(module, 10).Where(p => p.Key == ThemeModules.Jigsaw).Select(p => p.Value).FirstOrDefault();
            var planRow = tableService.GetTable<PlanData>().FirstOrDefault(r => TableFields.U(r, 1) == plan);
            return planRow is null ? 0 : (long)TableFields.U64(planRow, 4);
        }

        public override void HandleJigsawInfo(CS_JigsawInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_JigsawInfo reply = new() { ThemeId = request.ThemeId };
            foreach ((uint pic, bool claimed) in Load(connection.Account.Uid, request.ThemeId))
                reply.FPDKCBNKCAB[pic] = claimed;
            connection.Send(reply);
        }

        public override void HandleJigsawCompleteOnePic(CS_JigsawCompleteOnePic request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            if (ThemeModules.Config(tableService, request.ThemeId, ThemeModules.Jigsaw) == 0)
            {
                connection.Refuse(MsgId.MsgCsJigsawCompleteOnePic, HNINDAOJCKP.JigsawNotOpen);
                return;
            }

            var pic = Picture(request.ThemeId, request.HFGNEPFLJMD);
            // ASSUMPTION: the opening time counts from the PlanData start in the table, as the client computes it (start + minutes * 60)
            if (pic is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() < PlanStart(request.ThemeId) + TableFields.U(pic, 4) * 60L)
            {
                connection.Refuse(MsgId.MsgCsJigsawCompleteOnePic, HNINDAOJCKP.JigsawPicIdNotOpen);
                return;
            }

            lock (AccountLock.For(uid))
            {
                Dictionary<uint, bool> done = Load(uid, request.ThemeId);
                if (done.ContainsKey(request.HFGNEPFLJMD))
                {
                    connection.Refuse(MsgId.MsgCsJigsawCompleteOnePic, HNINDAOJCKP.JigsawPicAlreadyCompleted);
                    return;
                }

                done[request.HFGNEPFLJMD] = false;
                accountState.Set(uid, Key(request.ThemeId), done);
            }

            connection.Send(new SC_JigsawCompleteOnePic { ThemeId = request.ThemeId, HFGNEPFLJMD = request.HFGNEPFLJMD });
        }

        public override void HandleJigsawClaimReward(CS_JigsawClaimReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            var pic = Picture(request.ThemeId, request.HFGNEPFLJMD);
            if (pic is null)
            {
                connection.Refuse(MsgId.MsgCsJigsawClaimReward, HNINDAOJCKP.JigsawPicIdNotOpen);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [];
            using var drops = RewardDrops.Begin();
            lock (AccountLock.For(uid))
            {
                Dictionary<uint, bool> done = Load(uid, request.ThemeId);
                if (!done.TryGetValue(request.HFGNEPFLJMD, out bool claimed))
                {
                    connection.Refuse(MsgId.MsgCsJigsawClaimReward, HNINDAOJCKP.JigsawPicIdNotComplete);
                    return;
                }
                if (claimed)
                {
                    connection.Refuse(MsgId.MsgCsJigsawClaimReward, HNINDAOJCKP.JigsawRewardClaimed);
                    return;
                }

                foreach ((uint item, uint count) in TableFields.Map(pic, 5))
                    if (item != 0 && count > 0)
                        rewardService.Grant(uid, item, (int)count, messages);
                done[request.HFGNEPFLJMD] = true;
                accountState.Set(uid, Key(request.ThemeId), done);
            }

            drops.Finish(messages);
            messages.Add(new SC_JigsawClaimReward { ThemeId = request.ThemeId, HFGNEPFLJMD = request.HFGNEPFLJMD });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
