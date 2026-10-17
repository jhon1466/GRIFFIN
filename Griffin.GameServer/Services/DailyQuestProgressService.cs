using Griffin.Common.Table;

namespace Griffin.GameServer.Services;

public interface IDailyQuestProgressService : IGameService
{
    uint GetProgress(uint accountUid, uint questId);

    bool IsComplete(uint accountUid, uint questId);

    uint GetPoints(uint accountUid);

    void AddCondition(uint accountUid, uint conditionClass, string? link = null, uint amount = 1);
}

/// <summary>
/// Persists daily mission counters by account and China-local date. In this client table build,
/// DailyQuestData field 10 is conditionClass and field 11 is conditionNum; the checked-in proto names
/// those fields Reward and MFGPFKBGFKF, respectively.
/// </summary>
public sealed class DailyQuestProgressService(
    ITableService tableService,
    IAccountStateService accountState,
    IBattlepassService? battlepass = null) : IDailyQuestProgressService
{
    private const string StateKey = "quest.daily.progress";

    /// <summary>
    /// Condition classes (<c>DailyQuestData</c> col 10, meaning read from the quest texts 6252-6279) the server counts: 400 login, 750 lounge,
    /// 2/1000 stamina spent, 201/3800 directed research wins (stage type 41), 201/21001 daily duty wins (type 42), 209/1098 supply battle wins
    /// (type 14; ASSUMPTION: "supply" = StageData type 14), 1303/4015 gifts given, 1504/41001 control room dispatches, 3621/32007 dishes
    /// cooked and 4110/32017 plant waterings. ASSUMPTION: the rest (301 live-troop exercise = PvP, 407 puzzle exchange, 1118 Darkov front) belong
    /// to modes or requests the server does not run, so they count as DONE (before this a "claim all" answered "claimed" and paid nothing).
    /// </summary>
    private static bool IsCounted(DailyQuestData row) =>
        row.Reward is 400 or 750 || (row.Reward == 2 && row.Link == "1000") || (row.Reward == 201 && row.Link is "3800" or "21001")
        || (row.Reward == 209 && row.Link == "1098") || (row.Reward == 1303 && row.Link == "4015") || (row.Reward == 1504 && row.Link == "41001")
        || (row.Reward == 3621 && row.Link == "32007") || (row.Reward == 4110 && row.Link == "32017");
    private const uint PointsPerCompletedMission = 20;
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> Locks = new();

    public uint GetProgress(uint accountUid, uint questId)
    {
        DailyQuestData? row = Find(questId);
        if (row is null)
            return 0;

        if (!IsCounted(row))
            return row.MFGPFKBGFKF;

        DailyQuestProgressState state = GetState(accountUid);
        return Math.Min(state.Progress.GetValueOrDefault(questId), row.MFGPFKBGFKF);
    }

    public bool IsComplete(uint accountUid, uint questId)
    {
        DailyQuestData? row = Find(questId);
        return row is not null && row.MFGPFKBGFKF > 0 && GetProgress(accountUid, questId) >= row.MFGPFKBGFKF;
    }

    public uint GetPoints(uint accountUid)
    {
        var completed = tableService.GetTable<DailyQuestData>()
            .Count(row => row.MFGPFKBGFKF > 0 && GetProgress(accountUid, row.Id) >= row.MFGPFKBGFKF);
        return checked((uint)completed * PointsPerCompletedMission);
    }

    public void AddCondition(uint accountUid, uint conditionClass, string? link = null, uint amount = 1)
    {
        if (conditionClass == 0 || amount == 0)
            return;

        battlepass?.OnCondition(accountUid, conditionClass, link, amount);
        lock (Locks.GetOrAdd(accountUid, static _ => new object()))
        {
            DailyQuestProgressState state = GetState(accountUid);
            foreach (DailyQuestData row in tableService.GetTable<DailyQuestData>()
                         .Where(candidate => candidate.Reward == conditionClass
                             && (link is null || candidate.Link == link)))
            {
                if (row.MFGPFKBGFKF == 0)
                    continue;

                uint current = state.Progress.GetValueOrDefault(row.Id);
                state.Progress[row.Id] = (uint)Math.Min((ulong)row.MFGPFKBGFKF, (ulong)current + amount);
            }

            accountState.Set(accountUid, StateKey, state);
        }
    }

    private DailyQuestData? Find(uint questId) =>
        tableService.GetTable<DailyQuestData>().FirstOrDefault(row => row.Id == questId);

    private DailyQuestProgressState GetState(uint accountUid)
    {
        DailyQuestProgressState? state = accountState.Get<DailyQuestProgressState>(accountUid, StateKey);
        string today = GameDay.Key();
        if (state is null || state.Day != today)
            state = new DailyQuestProgressState { Day = today };
        return state;
    }
}

public sealed class DailyQuestProgressState
{
    public string Day { get; set; } = string.Empty;

    public Dictionary<uint, uint> Progress { get; set; } = [];
}
