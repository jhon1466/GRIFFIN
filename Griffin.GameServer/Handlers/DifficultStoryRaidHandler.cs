using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Resolves table-backed daily difficult-stage raids for the authenticated account.</summary>
    public sealed class DifficultStoryRaidHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IResourceService resources,
        IStaminaService staminaService,
        IRewardService rewardService) : DifficultStoryRaidHandlerBase
    {

        public override void HandleDifficultStoryRaid(CS_DifficultStoryRaid request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            DifficultyStoryData? stage = tableService.GetTable<DifficultyStoryData>()
                .FirstOrDefault(row => row.Id == request.StageId);
            if (stage is null || request.Num == 0)
            {
                connection.Send(new SC_DifficultStoryRaid { StageId = request.StageId });
                return;
            }

            lock (DifficultAccountLocks.For(uid))
            {
                Dictionary<uint, DifficultHandler.DifficultStoryState> storyState =
                    accountState.Get<Dictionary<uint, DifficultHandler.DifficultStoryState>>(uid, "difficult.stories") ?? [];
                DifficultHandler.DifficultStoryState access = storyState.GetValueOrDefault(stage.Id)
                    ?? new DifficultHandler.DifficultStoryState();
                if (!access.Unlocked && stage.NextId != 0)
                {
                    connection.Send(new SC_DifficultStoryRaid { StageId = stage.Id });
                    return;
                }

                DateOnly today = DateOnly.FromDateTime(GameDay.Now().DateTime);
                DifficultDailyState daily = accountState.Get<DifficultDailyState>(uid, "difficult.story_raid_daily") ?? new();
                if (daily.Date != today)
                    daily = new DifficultDailyState { Date = today };

                uint limit = TableFields.U(stage, 25);
                uint completedToday = daily.Counts.GetValueOrDefault(stage.Id);
                uint accepted = Math.Min(request.Num, completedToday >= limit ? 0 : limit - completedToday);
                if (accepted == 0)
                {
                    connection.Send(new SC_DifficultStoryRaid { StageId = stage.Id });
                    return;
                }

                Dictionary<uint, int> perRaidCost = ToIntMap(TableFields.Map(stage, 24));
                Dictionary<uint, int> totalCost = Scale(perRaidCost, accepted);
                int staminaCost = totalCost.TryGetValue(101, out int configuredStaminaCost) ? configuredStaminaCost : 0;
                Dictionary<uint, int> resourceCost = totalCost.Where(pair => pair.Key != 101).ToDictionary(pair => pair.Key, pair => pair.Value);
                SC_ResourceUpdate costUpdate = new();
                if (resourceCost.Count > 0 && !resources.TrySpend(uid, resourceCost, out costUpdate))
                {
                    connection.Send(new SC_DifficultStoryRaid { StageId = stage.Id });
                    return;
                }

                StaminaBalance? staminaBalance = null;
                if (staminaCost > 0)
                {
                    if (!staminaService.TrySpend(uid, staminaCost, StaminaService.StartingValue,
                            connection.GameClockSeconds(), out StaminaBalance paidBalance))
                    {
                        if (resourceCost.Count > 0)
                            resources.Add(uid, resourceCost);
                        connection.Send(new SC_DifficultStoryRaid { StageId = stage.Id });
                        return;
                    }
                    staminaBalance = paidBalance;
                }

                void RefundCost()
                {
                    if (resourceCost.Count > 0)
                        resources.Add(uid, resourceCost);
                    if (staminaCost > 0)
                        staminaService.Add(uid, staminaCost, StaminaService.StartingValue, connection.GameClockSeconds());
                }

                Dictionary<uint, int> perRaidRewards = ToIntMap(TableFields.Map(stage, 26));
                if (perRaidRewards.Count == 0)
                {
                    // The configured row must have a reward before stamina is consumed.
                    RefundCost();
                    Log.Warning("Difficult stage {StageId} has no parsed sweep reward; raid rejected.", stage.Id);
                    connection.Send(new SC_DifficultStoryRaid { StageId = stage.Id });
                    return;
                }

                Dictionary<uint, int> totalRewards = Scale(perRaidRewards, accepted);
                List<IMessage> updates = [];
                var drop = new StageDrop();
                var package = new StageDrop.Types.DropCache { DropType = StageDrop.Types.DropType.DropPackage };
                foreach ((uint itemId, int count) in totalRewards)
                {
                    GrantResult result = rewardService.Grant(uid, itemId, count, updates);
                    if (!result.Granted)
                        continue;

                    package.Cache.Add(new UserDropCache { ItemId = itemId, ItemNum = (uint)count });
                }

                if (package.Cache.Count == 0)
                {
                    RefundCost();
                    Log.Warning("Difficult stage {StageId} sweep items could not be granted; cost refunded.", stage.Id);
                    connection.Send(new SC_DifficultStoryRaid { StageId = stage.Id });
                    return;
                }

                drop.DropCache.Add(package);
                daily.Counts[stage.Id] = checked(completedToday + accepted);
                accountState.Set(uid, "difficult.story_raid_daily", daily);

                Dictionary<uint, uint> totals = accountState.Get<Dictionary<uint, uint>>(uid, "difficult.story_raids") ?? [];
                totals[stage.Id] = checked(totals.GetValueOrDefault(stage.Id) + accepted);
                accountState.Set(uid, "difficult.story_raids", totals);

                if (resourceCost.Count > 0)
                    connection.Send(costUpdate);
                if (staminaCost > 0 && staminaBalance is not null)
                    connection.SendRawPacket((ushort)MsgId.MsgScStaminaTypeResourceUpdate,
                        StaminaService.BuildUpdatePayload(101, staminaBalance));
                connection.Send(new SC_DifficultStoryRaid
                {
                    StageId = stage.Id,
                    Num = accepted,
                    Drop = new StageTotalDrop { Drop = drop },
                });
                foreach (IMessage update in updates)
                    connection.Send(update);

                Log.Information("Account {Uid} swept difficult stage {StageId} {Count} time(s).", uid, stage.Id, accepted);
            }
        }

        private static Dictionary<uint, int> ToIntMap(IReadOnlyList<(uint Key, uint Value)> pairs) =>
            pairs.Where(pair => pair.Key > 0 && pair.Value > 0)
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => checked((int)group.Sum(pair => (long)pair.Value)));

        private static Dictionary<uint, int> Scale(IReadOnlyDictionary<uint, int> values, uint multiplier) =>
            values.ToDictionary(pair => pair.Key, pair => checked(pair.Value * (int)multiplier));

        public sealed class DifficultDailyState
        {
            public DateOnly Date { get; set; }
            public Dictionary<uint, uint> Counts { get; set; } = [];
        }
    }
}
