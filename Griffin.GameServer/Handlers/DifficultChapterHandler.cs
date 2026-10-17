using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers;

/// <summary>Handles chapter-wide difficult-mode raids and tier reward claims.</summary>
public sealed class DifficultChapterHandler(
    ITableService tableService,
    IAccountStateService accountState,
    IResourceService resources,
    IStaminaService staminaService,
    IRewardService rewardService) : DifficultChapterHandlerBase
{
    private const string ChapterRaidStateKey = "difficult.chapter_raids";
    internal const string ClaimedStateKey = "difficult.chapter_rewards.claimed";

    public override void HandleDifficultChapterRaid(CS_DifficultChapterRaid request, Connection connection)
    {
        if (connection.Account is null)
            return;

        DifficultyChapterData? chapter = Chapters().FirstOrDefault(row => row.Id == request.Chapter);
        if (chapter is null || request.Num == 0)
        {
            connection.Send(new SC_DifficultChapterRaid { Chapter = request.Chapter });
            return;
        }

        uint uid = connection.Account.Uid;
        lock (DifficultAccountLocks.For(uid))
        {
            Dictionary<uint, uint> totals = accountState.Get<Dictionary<uint, uint>>(uid, ChapterRaidStateKey) ?? [];
            uint previous = totals.GetValueOrDefault(chapter.Id);
            uint limit = chapter.SweepsTimes;
            uint accepted = limit == 0 ? request.Num : Math.Min(request.Num, previous >= limit ? 0 : limit - previous);
            if (accepted == 0)
            {
                connection.Send(new SC_DifficultChapterRaid { Chapter = chapter.Id, TotalNum = previous, TotalRaid = TotalRaid(uid) });
                return;
            }

            Dictionary<uint, int> cost = Scale(ToIntMap(chapter.SweepsCost), accepted);
            int staminaCost = cost.TryGetValue(101, out int configuredStaminaCost) ? configuredStaminaCost : 0;
            Dictionary<uint, int> resourceCost = cost.Where(pair => pair.Key != 101).ToDictionary(pair => pair.Key, pair => pair.Value);
            SC_ResourceUpdate? update = null;
            if (resourceCost.Count > 0 && !resources.TrySpend(uid, resourceCost, out update))
            {
                connection.Send(new SC_DifficultChapterRaid { Chapter = chapter.Id, TotalNum = previous, TotalRaid = TotalRaid(uid) });
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
                    connection.Send(new SC_DifficultChapterRaid { Chapter = chapter.Id, TotalNum = previous, TotalRaid = TotalRaid(uid) });
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

            Dictionary<uint, int> configuredRewards = ToIntMap(chapter.SweepsReward.BAALJFJDGBB, chapter.SweepsReward.Value);
            if (configuredRewards.Count == 0)
            {
                RefundCost();
                Log.Warning("Difficult chapter {ChapterId} has no parsed sweep reward; raid rejected.", chapter.Id);
                connection.Send(new SC_DifficultChapterRaid { Chapter = chapter.Id, TotalNum = previous, TotalRaid = TotalRaid(uid) });
                return;
            }

            List<IMessage> updates = [];
            bool anyGranted = false;
            foreach ((uint itemId, int count) in Scale(configuredRewards, accepted))
                anyGranted |= rewardService.Grant(uid, itemId, count, updates).Granted;

            if (!anyGranted)
            {
                RefundCost();
                Log.Warning("Difficult chapter {ChapterId} sweep rewards could not be granted; cost refunded.", chapter.Id);
                connection.Send(new SC_DifficultChapterRaid { Chapter = chapter.Id, TotalNum = previous, TotalRaid = TotalRaid(uid) });
                return;
            }

            totals[chapter.Id] = checked(previous + accepted);
            accountState.Set(uid, ChapterRaidStateKey, totals);

            if (resourceCost.Count > 0)
                connection.Send(update!);
            if (staminaCost > 0 && staminaBalance is not null)
                connection.SendRawPacket((ushort)MsgId.MsgScStaminaTypeResourceUpdate,
                    StaminaService.BuildUpdatePayload(101, staminaBalance));
            connection.Send(new SC_DifficultChapterRaid
            {
                Chapter = chapter.Id,
                TotalNum = totals[chapter.Id],
                TotalRaid = TotalRaid(uid),
            });
            foreach (IMessage itemUpdate in updates)
                connection.Send(itemUpdate);
        }
    }

    public override void HandleDifficultChapterTakeReward(CS_DifficultChapterTakeReward request, Connection connection)
    {
        if (connection.Account is null)
            return;

        DifficultyChapterData? chapter = Chapters().FirstOrDefault(row => row.Id == request.ChapterId);
        if (chapter is null)
        {
            connection.Send(new SC_DifficultChapterTakeReward { ChapterId = request.ChapterId });
            return;
        }

        uint uid = connection.Account.Uid;
        lock (DifficultAccountLocks.For(uid))
        {
            Dictionary<string, HashSet<uint>> claims = accountState.Get<Dictionary<string, HashSet<uint>>>(uid, ClaimedStateKey) ?? [];
            string chapterKey = chapter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!claims.TryGetValue(chapterKey, out HashSet<uint>? claimed))
                claims[chapterKey] = claimed = [];

            Dictionary<uint, uint> raids = accountState.Get<Dictionary<uint, uint>>(uid, ChapterRaidStateKey) ?? [];
            uint progress = raids.GetValueOrDefault(chapter.Id);
            DifficultyStoryByChapterData? relation = tableService.GetTable<DifficultyStoryByChapterData>()
                .FirstOrDefault(row => row.Chapter == chapter.Id);
            if (relation is not null)
            {
                Dictionary<uint, uint> storyRaids = accountState.Get<Dictionary<uint, uint>>(uid, "difficult.story_raids") ?? [];
                progress = checked(progress + relation.Id.Aggregate(0u,
                    (sum, storyId) => checked(sum + storyRaids.GetValueOrDefault(storyId))));
            }
            HashSet<uint> grantedIds = [];
            List<IMessage> updates = [];
            foreach (uint rewardId in request.RewardIds.Distinct())
            {
                uint tier = RewardTierIndex(rewardId);
                if (tier >= chapter.ChapterRewardValue.Count || claimed.Contains(rewardId))
                    continue;

                if (progress < chapter.ChapterRewardValue[(int)tier])
                    continue;

                string[] tiers = (chapter.ChapterReward ?? string.Empty).Split('|', StringSplitOptions.TrimEntries);
                Dictionary<uint, int> rewards = tier < tiers.Length ? ParseReward(tiers[tier]) : [];
                if (rewards.Count == 0)
                    continue;

                bool anyGranted = false;
                foreach ((uint itemId, int count) in rewards)
                    anyGranted |= rewardService.Grant(uid, itemId, count, updates).Granted;
                if (!anyGranted)
                    continue;

                claimed.Add(rewardId);
                grantedIds.Add(rewardId);
            }

            if (grantedIds.Count > 0)
                accountState.Set(uid, ClaimedStateKey, claims);

            updates.Add(new SC_DifficultChapterTakeReward
            {
                ChapterId = chapter.Id,
                RewardIds = { grantedIds.Order() },
            });
            connection.SendAutoEncrypted(updates.ToArray());
        }
    }

    private IEnumerable<DifficultyChapterData> Chapters() => tableService.GetTable<DifficultyChapterData>();

    private uint TotalRaid(uint uid)
    {
        Dictionary<uint, uint> chapterRaids = accountState.Get<Dictionary<uint, uint>>(uid, ChapterRaidStateKey) ?? [];
        Dictionary<uint, uint> storyRaids = accountState.Get<Dictionary<uint, uint>>(uid, "difficult.story_raids") ?? [];
        return chapterRaids.Values.Concat(storyRaids.Values).Aggregate(0u, (sum, value) => checked(sum + value));
    }

    private static Dictionary<uint, int> ToIntMap(IEnumerable<KeyValuePair<uint, uint>> values) => values
        .Where(pair => pair.Key > 0 && pair.Value > 0)
        .ToDictionary(pair => pair.Key, pair => checked((int)pair.Value));

    private static Dictionary<uint, int> ParseReward(string value)
    {
        Dictionary<uint, int> rewards = [];
        foreach (string entry in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = entry.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !uint.TryParse(pair[0], out uint itemId) || !int.TryParse(pair[1], out int count)
                || itemId == 0 || count <= 0)
                continue;
            rewards[itemId] = checked(rewards.GetValueOrDefault(itemId) + count);
        }
        return rewards;
    }

    internal static uint RewardTierIndex(uint rewardId) => rewardId switch
    {
        1 => 0,
        2 => 1,
        3 => 2,
        _ => uint.MaxValue,
    };

    private static Dictionary<uint, int> ToIntMap(IReadOnlyList<uint> itemIds, IReadOnlyList<uint> counts)
    {
        Dictionary<uint, int> result = [];
        for (int index = 0; index < Math.Min(itemIds.Count, counts.Count); index++)
        {
            if (itemIds[index] == 0 || counts[index] == 0)
                continue;
            result[itemIds[index]] = checked(result.GetValueOrDefault(itemIds[index]) + (int)counts[index]);
        }
        return result;
    }

    private static Dictionary<uint, int> Scale(IReadOnlyDictionary<uint, int> values, uint multiplier) =>
        values.ToDictionary(pair => pair.Key, pair => checked(pair.Value * (int)multiplier));
}
