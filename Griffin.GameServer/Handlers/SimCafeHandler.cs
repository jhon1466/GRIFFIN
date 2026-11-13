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
    /// SimCafe in client 4.0.5142. Every cafe request (orders, upgrades, synthesis, store, NPC gifts, ...) is marked deprecated in the protocol
    /// and the client no longer sends them; the only live part is the warm-up ("preheat") board of the theme (NIPDKHAAPIC / MPDGHJDINCI):
    /// - the theme's ActivityModuleData column 4 maps module 3001 to a SimTaskConfigData row (3 = reward group, 4 = task group);
    /// - SimTaskListData (2 = task group, 3 = day, 4 = quest ids, quest counter type SimCafeWarmUp) lists the daily tasks of each day;
    /// - SimTaskRewardData (1 id, 2 = reward group, 3 = rank, 4 = points needed, 5 = items as two parallel lists) are the score rewards.
    /// The warm-up state (PIBACHAPBJH) is: 1 current day, 2 unused by the client, 3 rank -> taken, 4 score, 5 next refresh time.
    /// CS_SimCafeTakeWarmUpReward carries a SimTaskRewardData id; the client marks its rank taken when the reply arrives.
    /// </summary>
    public sealed class SimCafeHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : SimCafeHandlerBase
    {
        public sealed class State
        {
            public uint Day { get; set; }
            public long GameDay { get; set; }
            public uint Score { get; set; }
            public Dictionary<uint, bool> Taken { get; set; } = [];
        }

        private static string Key(uint config) => $"simcafe.warmup.{config}";

        /// <summary>The SimTaskConfigData id of the theme (module 3001).</summary>
        private uint Config(uint theme)
        {
            uint config = ThemeModules.Config(tableService, theme, ThemeModules.SimCafeWarmUp);
            // ASSUMPTION: when the theme has no module 3001 the theme id itself is used, as the client does in NIPDKHAAPIC.OHIDLMMHJLM
            // (SimTaskConfigData.GetDataById(themeId)); in every table row the config id equals the theme id.
            return config != 0 ? config : theme;
        }

        private IMessage? ConfigRow(uint config) =>
            tableService.GetTable<SimTaskConfigData>().FirstOrDefault(r => TableFields.U(r, 1) == config);

        /// <summary>Highest day of the task group in SimTaskListData (0 when the group has no rows).</summary>
        private uint LastDay(uint taskGroup) =>
            tableService.GetTable<SimTaskListData>().Where(r => TableFields.U(r, 2) == taskGroup).Select(r => TableFields.U(r, 3)).DefaultIfEmpty(0u).Max();

        /// <summary>Loads the state and advances the day once per game day.</summary>
        private State Load(uint uid, uint config)
        {
            State state = accountState.Get<State>(uid, Key(config)) ?? new State();
            long today = GameDay.Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (state.Day == 0)
            {
                state.Day = 1;
                state.GameDay = today;
                accountState.Set(uid, Key(config), state);
            }
            else if (today > state.GameDay)
            {
                // ASSUMPTION: the board moves to the next day's tasks once per game day (05:00 CST reset); the day stops at the last day
                // the task group has rows for, since the client finds no SimTaskListData row past it.
                IMessage? row = ConfigRow(config);
                uint last = row is null ? 0 : LastDay(TableFields.U(row, 4));
                uint next = state.Day + (uint)Math.Min(today - state.GameDay, uint.MaxValue);
                state.Day = last == 0 ? next : Math.Min(next, Math.Max(last, state.Day));
                state.GameDay = today;
                accountState.Set(uid, Key(config), state);
            }
            return state;
        }

        private static PIBACHAPBJH Info(State state)
        {
            // ASSUMPTION: field 5 is the time of the next daily refresh (the only capture had a future unix time there)
            long nextReset = (GameDay.Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + 1) * 86400 - (long)GameDay.ResetShift.TotalSeconds;
            PIBACHAPBJH info = new()
            {
                NCINIEDEDFG = state.Day,
                Score = state.Score,
                BPJMFIBFGKC = nextReset,
            };
            foreach ((uint rank, bool taken) in state.Taken)
                info.FNOAGJBJGGG[rank] = taken;
            return info;
        }

        /// <summary>
        /// The client stores the warm-up info by theme and reads field 1 unconditionally (NIPDKHAAPIC.EOMJOLJPGDN), so every theme gets an
        /// answer with the state, even a theme without warm-up tables.
        /// </summary>
        public override void HandleGetSimCafeWarmUpInfo(CS_GetSimCafeWarmUpInfo request, Connection connection)
        {
            State state;
            if (connection.Account is null)
                state = new State { Day = 1 };
            else
                lock (AccountLock.For(connection.Account.Uid))
                    state = Load(connection.Account.Uid, Config(request.ThemeId));
            connection.Send(new SC_GetSimCafeWarmUpInfo { ECOOFMAFCBG = Info(state), ThemeId = request.ThemeId });
        }

        /// <summary>
        /// Claims a score reward: the row must belong to the reward group of the theme's config, its rank must not be taken yet, and the
        /// score must reach its points (the client shows it claimable when score >= column 4, NIPDKHAAPIC.NAJLEIMDKJD).
        /// </summary>
        public override void HandleSimCafeTakeWarmUpReward(CS_SimCafeTakeWarmUpReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            uint config = Config(request.ThemeId);
            IMessage? configRow = ConfigRow(config);
            IMessage? reward = tableService.GetTable<SimTaskRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == request.RewardId);
            if (configRow is null || reward is null || TableFields.U(reward, 2) != TableFields.U(configRow, 3))
            {
                connection.Refuse(MsgId.MsgCsSimCafeTakeWarmUpReward, HNINDAOJCKP.SimCafeNoGiftRecord);
                return;
            }

            uint rank = TableFields.U(reward, 3);
            List<IMessage> messages = [];
            using (RewardDrops drops = RewardDrops.Begin())
            {
                lock (AccountLock.For(uid))
                {
                    State state = Load(uid, config);
                    if (state.Taken.GetValueOrDefault(rank))
                    {
                        connection.Refuse(MsgId.MsgCsSimCafeTakeWarmUpReward, HNINDAOJCKP.SimCafeRewardClaimed);
                        return;
                    }
                    if (state.Score < TableFields.U(reward, 4))
                    {
                        connection.Refuse(MsgId.MsgCsSimCafeTakeWarmUpReward, HNINDAOJCKP.SimCafeRewardPointNotEnough);
                        return;
                    }
                    foreach ((uint item, uint count) in TableFields.Map(reward, 5))
                        if (item != 0 && count > 0)
                            rewardService.Grant(uid, item, (int)count, messages);
                    state.Taken[rank] = true;
                    accountState.Set(uid, Key(config), state);
                }
                drops.Finish(messages);
            }
            messages.Add(new SC_SimCafeTakeWarmUpReward { RewardId = request.RewardId, ThemeId = request.ThemeId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>
        /// Adds warm-up points (SimTaskIndexData column 6 of a finished task) and tells the client (SC_SimCafeWarmUpScoreUpdate). Meant
        /// for the quest system once SimCafeWarmUp quests are tracked; nothing calls it yet.
        /// </summary>
        public void AddScore(Connection connection, uint theme, uint points)
        {
            if (connection.Account is null || points == 0)
                return;
            uint uid = connection.Account.Uid;
            uint config = Config(theme);
            uint score;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, config);
                state.Score += points;
                score = state.Score;
                accountState.Set(uid, Key(config), state);
            }
            connection.Send(new SC_SimCafeWarmUpScoreUpdate { Score = score, ThemeId = theme });
        }
    }
}
