using System.Numerics;
using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Puzzle challenge of the events (PazzleStage, module 20005 of the theme), as read from client 4.0.5142 (EDBHHEBEFLL data manager,
    /// MKGJIFIHGDJ panel). The theme maps module 20005 to a PazzleMissionData row (1 id, 2 star reward group, 3 PazzleStageData ids).
    /// Each PazzleStageData row (1 id, 2 minutes after the plan start, 3 previous stage, 4 StageData id) is a normal battle stage: the
    /// stars of the mission are the set bits of the stage records (complete_challenges) of those battle stages. PazzleStarRewardData
    /// (1 id, 2 star thresholds, 3 rewards per threshold split by ';') holds the star rewards; the client sends the threshold itself
    /// (KNAHJGMNKIL) and Info answers the claimed thresholds (FPDKCBNKCAB).
    /// </summary>
    public sealed class PazzleStageHandler(IAccountStateService accountState, ITableService tables, IRewardService rewards) : PazzleStageHandlerBase
    {
        private static string Key(uint mission) => $"pazzle.claimed.{mission}";

        private List<uint> Claimed(uint uid, uint mission) => accountState.Get<List<uint>>(uid, Key(mission)) ?? [];

        private PazzleMissionData? Mission(uint themeId)
        {
            uint config = ThemeModules.Config(tables, themeId, ThemeModules.PazzleStage);
            return config == 0 ? null : tables.GetTable<PazzleMissionData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
        }

        /// <summary>Stars of a mission, as the client counts them (EDBHHEBEFLL.CNEBGLIJKHK).</summary>
        private int Stars(uint uid, PazzleMissionData mission)
        {
            var records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
            var stages = tables.GetTable<PazzleStageData>().ToDictionary(r => TableFields.U(r, 1), r => TableFields.U(r, 4));
            int stars = 0;
            foreach (uint pazzle in TableFields.List(mission, 3))
                if (stages.TryGetValue(pazzle, out uint stage) && records.TryGetValue(stage.ToString(), out StageRecord? record))
                    stars += BitOperations.PopCount(record.CompleteChallenges);
            return stars;
        }

        public override void HandlePazzleStageInfo(CS_PazzleStageInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (Mission(request.ThemeId) is not { } mission)
            {
                connection.Refuse(MsgId.MsgCsPazzleStageInfo, HNINDAOJCKP.PazzleStageNotOpen);
                return;
            }

            connection.Send(new SC_PazzleStageInfo
            {
                ThemeId = request.ThemeId,
                FPDKCBNKCAB = { Claimed(connection.Account.Uid, TableFields.U(mission, 1)).Order() },
            });
        }

        public override void HandlePazzleStageGetReward(CS_PazzleStageGetReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            if (Mission(request.ThemeId) is not { } mission)
            {
                connection.Refuse(MsgId.MsgCsPazzleStageGetReward, HNINDAOJCKP.PazzleStageNotOpen);
                return;
            }

            uint missionId = TableFields.U(mission, 1);
            var rewardRow = tables.GetTable<PazzleStarRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == TableFields.U(mission, 2));
            IReadOnlyList<uint> thresholds = rewardRow is null ? [] : TableFields.List(rewardRow, 2);
            int index = thresholds.ToList().IndexOf(request.KNAHJGMNKIL);
            if (rewardRow is null || index < 0)
            {
                connection.Refuse(MsgId.MsgCsPazzleStageGetReward, HNINDAOJCKP.PazzleStageNotOpen);
                return;
            }

            var messages = new List<IMessage>();
            using (var drops = RewardDrops.Begin())
            {
                lock (AccountLock.For(uid))
                {
                    List<uint> claimed = Claimed(uid, missionId);
                    if (claimed.Contains(request.KNAHJGMNKIL))
                    {
                        connection.Refuse(MsgId.MsgCsPazzleStageGetReward, HNINDAOJCKP.PazzleStageStarRewardClaimed);
                        return;
                    }

                    if (Stars(uid, mission) < request.KNAHJGMNKIL)
                    {
                        connection.Refuse(MsgId.MsgCsPazzleStageGetReward, HNINDAOJCKP.PazzleStageStarNotEnough);
                        return;
                    }

                    // the reward string of the threshold at the same position (client: GLEDMAELAAA split by ';', ',' and ':')
                    string[] tiers = TableFields.S(rewardRow, 3).Split(';');
                    if (index < tiers.Length)
                        foreach ((uint item, int count) in rewards.ParseRewards(tiers[index]))
                            rewards.Grant(uid, item, count, messages);

                    claimed.Add(request.KNAHJGMNKIL);
                    accountState.Set(uid, Key(missionId), claimed);
                }

                drops.Finish(messages);
            }

            messages.Add(new SC_PazzleStageGetReward { ThemeId = request.ThemeId, KNAHJGMNKIL = request.KNAHJGMNKIL });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
