using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Table-backed daily quest and point-reward claims. Claims require persisted daily progress and use table-defined
    /// rewards; supported actions update progress through IDailyQuestProgressService.
    /// Claims are persisted per account and China-local calendar day to prevent repeat grants.
    /// </summary>
    public sealed class CommonQuestHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IDailyQuestProgressService questProgress,
        IRewardService rewardService,
        IAchievementService achievements,
        ISimCombatWeekService week,
        ILobbyMiniGameService miniGames) : CommonQuestHandlerBase
    {
        private const string StateKey = "quest.daily.claims";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> AccountLocks = new();

        public override void HandleCommonQuestTakeReward(CS_CommonQuestTakeReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (request.Type == CommonQuestCounters.Types.Type.Achievement)
            {
                // Achievements known to the server (commander level, stage cleared): the reward of the complete ones is taken here.
                List<IMessage> achievementMessages = [];
                List<uint> taken = achievements.Claim(connection.Account.Uid, request.QuestId, achievementMessages);
                if (taken.Count == 0)
                {
                    connection.Refuse(MsgId.MsgCsCommonQuestTakeReward, HNINDAOJCKP.QuestCondNotMatch);
                    return;
                }

                achievementMessages.Add(new SC_CommonQuestTakeReward { Type = request.Type, QuestId = { taken } });
                connection.SendAutoEncrypted(achievementMessages.ToArray());
                return;
            }

            if (request.Type == CommonQuestCounters.Types.Type.LobbyMiniGame)
            {
                // the achievements of the Game Center (A-166): the reward of each complete one is item 1610000, paid once
                List<IMessage> gameMessages = [];
                List<uint> takenGame = miniGames.Claim(connection.Account.Uid, request.QuestId, gameMessages);
                if (takenGame.Count == 0)
                {
                    connection.Refuse(MsgId.MsgCsCommonQuestTakeReward, HNINDAOJCKP.QuestCondNotMatch);
                    return;
                }

                gameMessages.Insert(0, new SC_CommonQuestTakeReward { Type = request.Type, QuestId = { takenGame } });
                // the client only marks the taken ids in its phase-reward map: the counters follow so its reward map says "taken" too (otherwise the red dot of the button stays lit)
                gameMessages.Add(new SC_CommonQuestCountUpdate { Counters = miniGames.Counters(connection.Account.Uid) });
                connection.SendAutoEncrypted(gameMessages.ToArray());
                return;
            }

            if (request.Type == CommonQuestCounters.Types.Type.WeeklyChallenge)
            {
                // the mission objectives of 要塞伯爵 (A-136): complete ones are paid once
                List<IMessage> weekMessages = [];
                List<uint> takenWeek = week.ClaimQuests(connection.Account.Uid, request.QuestId, weekMessages);
                if (takenWeek.Count == 0)
                {
                    connection.Refuse(MsgId.MsgCsCommonQuestTakeReward, HNINDAOJCKP.QuestCondNotMatch);
                    return;
                }

                weekMessages.Add(new SC_CommonQuestTakeReward { Type = request.Type, QuestId = { takenWeek } });
                connection.SendAutoEncrypted(weekMessages.ToArray());
                return;
            }

            if (request.Type != CommonQuestCounters.Types.Type.Daily)
            {
                Log.Warning("Daily quest reward handler received unsupported quest type {Type} for account {Uid}.",
                    request.Type, connection.Account.Uid);
                // The other quest types (weekly challenge, achievements...) have no progress on this server: nothing is complete, and the client gets its own refusal instead of silence.
                connection.Refuse(MsgId.MsgCsCommonQuestTakeReward, HNINDAOJCKP.QuestCondNotMatch);
                return;
            }

            uint uid = connection.Account.Uid;
            lock (AccountLocks.GetOrAdd(uid, static _ => new object()))
            {
                DailyQuestClaimState state = GetState(uid);
                HashSet<uint> knownIds = tableService.GetTable<DailyQuestData>().Select(q => q.Id).ToHashSet();
                List<uint> accepted = request.QuestId
                    .Where(id => knownIds.Contains(id)
                        && !state.ClaimedQuests.Contains(id)
                        && questProgress.IsComplete(uid, id))
                    .Distinct()
                    .ToList();
                List<IMessage> messages = [];
                List<uint> granted = [];

                foreach (uint questId in accepted)
                {
                    DailyQuestData quest = tableService.GetTable<DailyQuestData>().First(q => q.Id == questId);
                    int before = messages.Count;
                    foreach ((uint itemId, uint count) in quest.RewardList)
                    {
                        if (count == 0 || count > int.MaxValue)
                            continue;
                        rewardService.Grant(uid, itemId, (int)count, messages);
                    }

                    // RewardService sends a delta for every table-backed resource/item/character/weapon it accepts.
                    // Never mark the quest claimed if none of its configured rewards was recognized.
                    if (messages.Count > before || quest.RewardList.Count == 0)
                        granted.Add(questId);
                }

                foreach (uint id in granted)
                    state.ClaimedQuests.Add(id);
                SaveState(uid, state);
                messages.Add(new SC_CommonQuestTakeReward
                {
                    Type = request.Type,
                    QuestId = { granted },
                });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        public override void HandleCommonQuestTakePhaseReward(CS_CommonQuestTakePhaseReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (request.Type != CommonQuestCounters.Types.Type.Daily)
            {
                Log.Warning("Daily quest phase handler received unsupported quest type {Type} for account {Uid}.",
                    request.Type, connection.Account.Uid);
                connection.Refuse(MsgId.MsgCsCommonQuestTakePhaseReward, HNINDAOJCKP.QuestPhaseCondNotMatch);
                return;
            }

            uint uid = connection.Account.Uid;
            lock (AccountLocks.GetOrAdd(uid, static _ => new object()))
            {
                DailyQuestClaimState state = GetState(uid);
                Dictionary<uint, uint> configuredPhases = tableService.GetTable<DailyRewardGroupData>()
                    .Where(group => group.Id == 1)
                    .SelectMany(group => group.RewardList)
                    .Select(id => tableService.GetTable<DailyRewardData>().FirstOrDefault(row => row.Id == id))
                    .Where(row => row is not null)
                    .ToDictionary(row => row!.Id, row => row!.Value);
                List<uint> accepted = request.PhaseId
                    .Where(id => configuredPhases.TryGetValue(id, out uint threshold)
                        && questProgress.GetPoints(uid) >= threshold
                        && !state.ClaimedPhases.Contains(id))
                    .Distinct()
                    .ToList();
                List<IMessage> messages = [];
                List<uint> granted = [];

                foreach (uint phaseId in accepted)
                {
                    DailyRewardData? reward = tableService.GetTable<DailyRewardData>().FirstOrDefault(r => r.Id == phaseId);
                    if (reward is null || reward.RewardList.Count == 0)
                        continue;

                    int before = messages.Count;
                    foreach ((uint itemId, uint amount) in reward.RewardList)
                    {
                        if (amount == 0 || amount > int.MaxValue)
                            continue;
                        rewardService.Grant(uid, itemId, (int)amount, messages);
                    }

                    if (messages.Count > before)
                        granted.Add(phaseId);
                }

                foreach (uint id in granted)
                    state.ClaimedPhases.Add(id);
                SaveState(uid, state);
                messages.Add(new SC_CommonQuestTakePhaseReward
                {
                    Type = request.Type,
                    PhaseId = { granted },
                });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        internal DailyQuestClaimState GetState(uint uid)
        {
            DailyQuestClaimState? state = accountState.Get<DailyQuestClaimState>(uid, StateKey);
            string today = ChinaDateKey();
            if (state is null || !string.Equals(state.Day, today, StringComparison.Ordinal))
                state = new DailyQuestClaimState { Day = today };
            return state;
        }

        internal void SaveState(uint uid, DailyQuestClaimState state) => accountState.Set(uid, StateKey, state);

        internal static string ChinaDateKey() => GameDay.Key();
    }

    public sealed class DailyQuestClaimState
    {
        public string Day { get; set; } = string.Empty;
        public HashSet<uint> ClaimedQuests { get; set; } = [];
        public HashSet<uint> ClaimedPhases { get; set; } = [];
    }
}
