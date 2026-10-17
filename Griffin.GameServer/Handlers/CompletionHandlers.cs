using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    // Requests that used to end in "function locked" (-10018). Each one now answers with its real contract; where the game
    // rules or the data that would give a reward are not known, the answer confirms the request and grants nothing (no invented rewards).

    /// <summary>AFK assistant: ends the session; the server keeps no AFK run, so the reply carries no results.</summary>
    public sealed class AfkAssistantHandler : AfkAssistantHandlerBase
    {
        public override void HandleAfkAssistantEnd(CS_AfkAssistantEnd request, Connection connection) =>
            connection.Send(new SC_AfkAssistantEnd());
    }

    /// <summary>
    /// Server-side battle channel (login and loading of a battle room). Campaign and practice battles run in the client, so there is no room:
    /// login and loading are confirmed and the in-battle messages that have no reply are accepted.
    /// </summary>
    public sealed class BattleHandler : BattleHandlerBase
    {
        public override void HandleBattleLogin(CS_BattleLogin request, Connection connection) => connection.Send(new SC_BattleLogin());

        public override void HandleBattleLoading(CS_BattleLoading request, Connection connection) => connection.Send(new SC_BattleLoading());

        public override void HandleBattleDeploymentModification(CS_BattleDeploymentModification request, Connection connection) { }

        public override void HandleBattlePlayerAction(CS_BattlePlayerAction request, Connection connection) { }

        public override void HandleBattlePlayerReadyForAction(CS_BattlePlayerReadyForAction request, Connection connection) { }
    }

    /// <summary>Hero (hard) mode of a chapter: the choice is confirmed back (the server keeps no hero-mode rules).</summary>
    public sealed class HeroModeHandler : HeroModeHandlerBase
    {
        public override void HandleChapterHeroMode(CS_ChapterHeroMode request, Connection connection) =>
            connection.Send(new SC_ChapterHeroMode { ChapterId = { request.ChapterId }, COFDFFJMNMA = request.COFDFFJMNMA });
    }

    /// <summary>Treasure (collection pass) level-up: reports the stored level and experience (the pass levels itself from its experience).</summary>
    public sealed class LvUpHandler(IAccountStateService accountState) : LvUpHandlerBase
    {
        public override void HandleTreasureLvUp(CS_TreasureLvUp request, Connection connection)
        {
            TreasurePassState state = connection.Account is null ? new TreasurePassState()
                : accountState.Get<TreasurePassState>(connection.Account.Uid, "treasure.pass.60015") ?? new TreasurePassState();
            connection.Send(new SC_TreasureLvUp { Level = state.Level, Exp = state.Exp });
        }
    }

    /// <summary>
    /// "Claim all" of several quest tabs at once. Only the daily tab exists on this server: every completed unclaimed daily quest and every
    /// reachable point reward is paid (same rules and state as <see cref="CommonQuestHandler"/>); the other tabs have nothing to claim.
    /// </summary>
    public sealed class MultiQuestTypeHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IDailyQuestProgressService questProgress,
        IRewardService rewardService) : MultiQuestTypeHandlerBase
    {
        public override void HandleMultiQuestTypeTakeReward(CS_MultiQuestTypeTakeReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            if (request.Types_.Contains(CommonQuestCounters.Types.Type.Daily))
            {
                DailyQuestClaimState state = accountState.Get<DailyQuestClaimState>(uid, "quest.daily.claims") ?? new DailyQuestClaimState();
                if (state.Day != CommonQuestHandler.ChinaDateKey())
                    state = new DailyQuestClaimState { Day = CommonQuestHandler.ChinaDateKey() };

                foreach (DailyQuestData quest in tableService.GetTable<DailyQuestData>())
                {
                    if (state.ClaimedQuests.Contains(quest.Id) || !questProgress.IsComplete(uid, quest.Id))
                        continue;
                    int before = messages.Count;
                    foreach ((uint item, uint count) in quest.RewardList)
                        if (count > 0 && count <= int.MaxValue)
                            rewardService.Grant(uid, item, (int)count, messages);
                    if (messages.Count > before || quest.RewardList.Count == 0)
                        state.ClaimedQuests.Add(quest.Id);
                }

                uint points = questProgress.GetPoints(uid);
                foreach (uint phaseId in tableService.GetTable<DailyRewardGroupData>().Where(g => g.Id == 1).SelectMany(g => g.RewardList))
                {
                    DailyRewardData? phase = tableService.GetTable<DailyRewardData>().FirstOrDefault(r => r.Id == phaseId);
                    if (phase is null || state.ClaimedPhases.Contains(phaseId) || points < phase.Value)
                        continue;
                    int before = messages.Count;
                    foreach ((uint item, uint count) in phase.RewardList)
                        if (count > 0 && count <= int.MaxValue)
                            rewardService.Grant(uid, item, (int)count, messages);
                    if (messages.Count > before)
                        state.ClaimedPhases.Add(phaseId);
                }

                accountState.Set(uid, "quest.daily.claims", state);
            }

            messages.Add(new SC_MultiQuestTypeTakeReward { Types_ = { request.Types_ } });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }

    /// <summary>Beginner event rewards: no reward table exists in the client data, so the request is confirmed and nothing is granted.</summary>
    public sealed class NewbeeHandler : NewbeeHandlerBase
    {
        public override void HandleNewbeeTakeFirstRewardV2(CS_NewbeeTakeFirstRewardV2 request, Connection connection) =>
            connection.Send(new SC_NewbeeTakeFirstRewardV2 { ONMNEMDCEIJ = request.ONMNEMDCEIJ });

        public override void HandleNewbeeTakeStepRewardV2(CS_NewbeeTakeStepRewardV2 request, Connection connection) =>
            connection.Send(new SC_NewbeeTakeStepRewardV2 { ONMNEMDCEIJ = request.ONMNEMDCEIJ, RewardId = request.RewardId });
    }

    /// <summary>
    /// Sweep ("Raid"): repeats a stage already passed without fighting. Costs the stage cost (<c>StageData</c> col 8/9, energy item 101 or an
    /// item) times the repetitions and pays <c>raid_times</c> x the normal drops of a victory (the same table drops, experience items and
    /// coin as <see cref="StageResultHandler"/>). ASSUMPTIONS: any passed stage can be swept (the official game asks for all stars), 1..10
    /// repetitions per request; first-clear rewards are never paid.
    /// </summary>
    public sealed class RaidHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService,
        IItemUseService itemUse,
        IStaminaService staminaService) : RaidHandlerBase
    {
        /// <summary>The real client asked for 24 sweeps of the weapon-experience stage in one go (log 2026-10-16), so the old cap of 10 refused a legitimate request; energy is the limit.</summary>
        private const uint MaxRaidTimes = 100;

        public override void HandleRaid(CS_Raid request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            StageData? stage = tableService.GetTable<StageData>().FirstOrDefault(s => s.Id == request.StcStageId);
            var records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
            bool passed = (records.TryGetValue(request.StcStageId.ToString(), out StageRecord? record) && record.FirstPassTime > 0)
                || StageHandler.ClearedStages.Any(s => s.StageId == request.StcStageId);
            if (stage is null || !passed || request.RaidTimes is < 1 or > MaxRaidTimes)
            {
                connection.Refuse(MsgId.MsgCsRaid, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<IMessage> messages = [];
            uint costItem = TableFields.U(stage, 8);
            ulong cost = (ulong)TableFields.U(stage, 9) * request.RaidTimes;
            if (costItem != 0 && cost != 0)
            {
                if (cost > int.MaxValue)
                {
                    connection.Refuse(MsgId.MsgCsRaid, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                if (costItem == 101)
                {
                    if (!staminaService.TrySpend(uid, (int)cost, StaminaService.StartingValue, connection.GameClockSeconds(), out StaminaBalance balance))
                    {
                        connection.Refuse(MsgId.MsgCsRaid, HNINDAOJCKP.StaminaNotEnough);
                        return;
                    }

                    connection.SendRawPacket((ushort)MsgId.MsgScStaminaTypeResourceUpdate, StaminaService.BuildUpdatePayload(101, balance));
                }
                else if (!itemUse.Spend(uid, new Dictionary<uint, int> { [costItem] = (int)cost }, messages))
                {
                    connection.Refuse(MsgId.MsgCsRaid, HNINDAOJCKP.ItemNotEnough);
                    return;
                }
            }

            Dictionary<uint, int> totals = [];
            for (uint i = 0; i < request.RaidTimes; i++)
            {
                foreach (uint package in stage.NormalDropList)
                    foreach ((uint item, int count) in itemUse.RollPackage(package))
                        totals[item] = totals.GetValueOrDefault(item) + count;
                foreach ((uint item, uint count) in stage.ExpReward)
                    if (count > 0)
                        totals[item] = totals.GetValueOrDefault(item) + (int)count;
                if (stage.Coin > 0)
                    totals[2u] = totals.GetValueOrDefault(2u) + (int)stage.Coin;
            }

            SC_Raid reply = new() { StcStageId = request.StcStageId, RaidTimes = request.RaidTimes, Drops = new StageTotalDrop { Drop = new StageDrop() } };
            StageDrop.Types.DropCache cache = new() { DropType = StageDrop.Types.DropType.DropPackage };
            foreach ((uint item, int count) in totals)
            {
                rewardService.Grant(uid, item, count, messages);
                cache.Cache.Add(new UserDropCache { ItemId = item, ItemNum = (uint)count });
            }

            reply.Drops.Drop.DropCache.Add(cache);
            Log.Information("Raid of stage {Stage} x{Times} for account {Uid}: {Items} item kinds.", request.StcStageId, request.RaidTimes, uid, totals.Count);
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
