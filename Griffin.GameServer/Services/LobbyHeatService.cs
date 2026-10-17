using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>Daily/weekly "heat" of the lobby as the client shows it (<c>SC_LobbyGetQuestInfo</c> and updates).</summary>
    public sealed record LobbyHeatSnapshot(uint Today, uint Week, bool FirstClaimed, IReadOnlyList<uint> Claimed, IReadOnlyDictionary<uint, uint> Counts);

    /// <summary>
    /// Lobby heat. Every lobby activity (interactions with a character, cooking, watering plants, upgrades, messages...) is a row of
    /// <c>LobbyDailyHeatData</c> (1 = source id, 2 = heat given, 3 = times per day that count, 0 = every time). The heat of a day is limited by
    /// <c>LobbyConfigData</c> col 15 (100) and the one of a week by col 16 (400); <c>LobbyWeeklyRewardData</c> (1 = id, 2 = heat needed,
    /// 3 = reward) pays the weekly rewards. ASSUMPTIONS (docs/assumptions.md A-17): those columns, the daily cap 100 / weekly cap 400, the
    /// Monday 05:00 China week start (same reset hour as <see cref="GameDay"/>), and that the "first interaction of the day" reward is
    /// <c>LobbyConfigData</c> col 13, paid with the first heat of the day.
    /// </summary>
    public interface ILobbyHeatService : IGameService
    {
        LobbyHeatSnapshot Get(uint uid);

        /// <summary>Counts an activity; returns the messages to send (heat update and the first-interaction reward), none when nothing counted.</summary>
        List<IMessage> Add(uint uid, uint source);

        /// <summary>Claims weekly rewards; returns the claimed ids (empty when nothing is claimable, <paramref name="error"/> says why).</summary>
        IReadOnlyList<uint> ClaimWeekly(uint uid, IEnumerable<uint> ids, List<IMessage> messages, out HNINDAOJCKP error);
    }

    public sealed class LobbyHeatService(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : ILobbyHeatService
    {
        private const string StateKey = "lobby.heat";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> Locks = new();

        public sealed class HeatState
        {
            public long Day { get; set; }
            public long Week { get; set; }
            public uint Today { get; set; }
            public uint WeekHeat { get; set; }
            public bool FirstClaimed { get; set; }
            public Dictionary<uint, uint> Counts { get; set; } = [];
            public List<uint> Claimed { get; set; } = [];
        }

        private IMessage? Config => tableService.GetTable<LobbyConfigData>().FirstOrDefault();

        private uint DailyCap => Config is { } c && TableFields.U(c, 15) is var v and > 0 ? v : 100;

        private uint WeeklyCap => Config is { } c && TableFields.U(c, 16) is var v and > 0 ? v : 400;

        private static long CurrentDay() => GameDay.Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        /// <summary>Monday of the game week (day 0 of the epoch was a Thursday).</summary>
        private static long WeekOf(long day) => day - ((day + 3) % 7);

        private HeatState Load(uint uid)
        {
            HeatState state = accountState.Get<HeatState>(uid, StateKey) ?? new HeatState();
            long day = CurrentDay();
            if (state.Day != day)
            {
                state.Day = day;
                state.Today = 0;
                state.FirstClaimed = false;
                state.Counts = [];
            }

            if (state.Week != WeekOf(day))
            {
                state.Week = WeekOf(day);
                state.WeekHeat = 0;
                state.Claimed = [];
            }

            return state;
        }

        public LobbyHeatSnapshot Get(uint uid)
        {
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                HeatState s = Load(uid);
                return new LobbyHeatSnapshot(s.Today, s.WeekHeat, s.FirstClaimed, s.Claimed.ToList(), new Dictionary<uint, uint>(s.Counts));
            }
        }

        public List<IMessage> Add(uint uid, uint source)
        {
            List<IMessage> messages = [];
            IMessage? row = tableService.GetTable<LobbyDailyHeatData>().FirstOrDefault(r => TableFields.U(r, 1) == source);
            if (row is null)
                return messages;

            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                HeatState state = Load(uid);
                uint limit = TableFields.U(row, 3);
                uint done = state.Counts.GetValueOrDefault(source);
                if (limit != 0 && done >= limit)
                    return messages;

                uint heat = Math.Min(TableFields.U(row, 2), Math.Min(DailyCap - Math.Min(DailyCap, state.Today), WeeklyCap - Math.Min(WeeklyCap, state.WeekHeat)));
                state.Counts[source] = done + 1;
                if (heat == 0)
                {
                    accountState.Set(uid, StateKey, state);
                    return messages;
                }

                state.Today += heat;
                state.WeekHeat += heat;
                if (!state.FirstClaimed)
                {
                    state.FirstClaimed = true;
                    if (Config is { } config)
                        foreach ((uint item, uint count) in TableFields.Map(config, 13))
                            rewardService.Grant(uid, item, (int)count, messages);
                }

                accountState.Set(uid, StateKey, state);
                messages.Add(new SC_LobbyQuestHeatUpdate
                {
                    IHGKOKKEKDJ = state.Today,
                    ELMAEDKKCGF = state.WeekHeat,
                    EKMDGLKBILM = source,
                    MODEFLLOFOL = heat,
                    NGCBPMPCMMJ = state.FirstClaimed,
                });
            }

            return messages;
        }

        public IReadOnlyList<uint> ClaimWeekly(uint uid, IEnumerable<uint> ids, List<IMessage> messages, out HNINDAOJCKP error)
        {
            error = HNINDAOJCKP.LobbyWeeklyHeatNotEnough;
            List<uint> done = [];
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                HeatState state = Load(uid);
                foreach (uint id in ids.Distinct())
                {
                    IMessage? row = tableService.GetTable<LobbyWeeklyRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == id);
                    if (row is null || state.WeekHeat < TableFields.U(row, 2))
                        continue;
                    if (state.Claimed.Contains(id))
                    {
                        error = HNINDAOJCKP.LobbyWeeklyRewardClaimed;
                        continue;
                    }

                    foreach ((uint item, uint count) in TableFields.Map(row, 3))
                        rewardService.Grant(uid, item, (int)count, messages);
                    state.Claimed.Add(id);
                    done.Add(id);
                }

                if (done.Count > 0)
                    accountState.Set(uid, StateKey, state);
            }

            return done;
        }

        /// <summary>Ids of the weekly rewards reached and not yet claimed.</summary>
        public IEnumerable<uint> Claimable(uint uid)
        {
            LobbyHeatSnapshot snapshot = Get(uid);
            return tableService.GetTable<LobbyWeeklyRewardData>()
                .Where(r => snapshot.Week >= TableFields.U(r, 2) && !snapshot.Claimed.Contains(TableFields.U(r, 1)))
                .Select(r => TableFields.U(r, 1));
        }
    }
}
