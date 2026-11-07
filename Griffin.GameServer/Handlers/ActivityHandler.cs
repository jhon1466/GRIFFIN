using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Extensions;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Google.Protobuf;

namespace Griffin.GameServer.Handlers
{
    public sealed class ActivityHandler(IEventSigninService eventSignin, IActivityBackService activityBack, ITableService tableService, IEventOverrideService eventOverrides, IRewardService rewardService, IAccountStateService accountState, IItemUseService itemUse) : ActivityHandlerBase
    {
        public override void HandlePlanActivity(CS_PlanActivity request, Connection connection)
        {
            ulong now = checked((ulong)connection.GameClockSeconds());
            SC_PlanActivity response = new() { Type = request.Type };

            if (request.Type is 4 or 5 or 13)
            {
                // These request types are PlanData.system values. PlanData's column 3 is
                // an enum; columns 4 and 5 carry the actual open/close Unix timestamps.
                List<PlanData> plans = tableService.GetTable<PlanData>()
                    .Where(row => TableFields.U(row, 2) == request.Type)
                    .Where(row => TableFields.U64(row, 4) <= now || TableFields.U64(row, 4) == 0)
                    .Where(row => TableFields.U64(row, 5) == 0 || now <= TableFields.U64(row, 5))
                    .OrderBy(row => TableFields.U(row, 1))
                    .ToList();
                List<PlanData> upcoming = tableService.GetTable<PlanData>()
                    .Where(row => TableFields.U(row, 2) == request.Type)
                    .Where(row => TableFields.U64(row, 4) > now)
                    .OrderBy(row => TableFields.U64(row, 4))
                    .ThenBy(row => TableFields.U(row, 1))
                    .ToList();

                response.ActiveIds.AddRange(plans.Select(row => TableFields.U(row, 1)));
                response.NextIds.AddRange(upcoming.Select(row => TableFields.U(row, 1)));
            }
            else if (request.Type == 6)
            {
                // the official dates, or the window of an event opened by hand from the GM tools (EventOverrideService)
                List<(ActivityListData Row, ulong Open, ulong Close)> rows = tableService.GetTable<ActivityListData>()
                    .Select(row => { (ulong open, ulong close) = eventOverrides.Effective(row); return (row, open, close); })
                    .ToList();
                List<(ActivityListData Row, ulong Open, ulong Close)> active = rows
                    .Where(r => r.Open == 0 || r.Open <= now)
                    .Where(r => r.Close == 0 || now <= r.Close)
                    .OrderBy(r => TableFields.U(r.Row, 1))
                    .ToList();
                List<(ActivityListData Row, ulong Open, ulong Close)> upcoming = rows
                    .Where(r => r.Open > now)
                    .OrderBy(r => r.Open)
                    .ThenBy(r => TableFields.U(r.Row, 1))
                    .ToList();

                response.ActiveIds.AddRange(active.Select(r => TableFields.U(r.Row, 1)));
                response.NextIds.AddRange(upcoming.Select(r => TableFields.U(r.Row, 1)));
                response.ActivityPlan.AddRange(active.Select(r => ToActivityPlan(r.Row, r.Open, r.Close)));
                response.WillOpenPlans.AddRange(upcoming.Select(r => ToActivityPlan(r.Row, r.Open, r.Close)));
            }

            // Unknown types deliberately receive an empty typed response. Do not reuse the
            // stale event IDs/dates captured from another account or server season.
            connection.Send(response);
        }

        private static ActivityPlan ToActivityPlan(ActivityListData row, ulong open, ulong close)
        {
            ActivityPlan plan = new()
            {
                Id = TableFields.U(row, 1),
                OpenTime = checked((long)open),
                CloseTime = checked((long)close),
            };
            plan.Args.AddRange(TableFields.List(row, 15));
            return plan;
        }
        public override void HandleActivityGetMedium(CS_ActivityGetMedium request, Connection connection)
        {
            // Medium/ad tokens are session-issued by the upstream service. Never reuse a
            // token captured from another account/session.
            connection.Send(new SC_ActivityGetMedium());
        }

        public override void HandleGetActivityAmo(CS_GetActivityAmo request, Connection connection)
        {
                    connection.Send(new SC_GetActivityAmo
                    {
                        OOEJEHGFCLL = { },
                    });
        }

        /// <summary>
        /// Daily meal of the events (PJBMOFEDKLG, ActivityListData type 12): column 11 gives the meals of the day ("slot:hh:mm;…", China
        /// time) and column 16 their reward ("slot:item:count;…", e.g. 60 stamina). The state is, per slot, the time it opens today and
        /// whether it was taken. The tables do carry the schedule; the meals were simply never served.
        /// </summary>
        private (ActivityListData Row, List<(uint Slot, long Opens, uint Item, int Count)> Meals)? DailyFood()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ActivityListData? row = tableService.GetTable<ActivityListData>()
                .FirstOrDefault(r => r.Type == 12 && r.OpenTime <= now && (r.CloseTime == 0 || now <= r.CloseTime));
            if (row is null)
                return null;

            DateTime chinaToday = DateTime.UtcNow.AddHours(8).Date;
            Dictionary<uint, (uint Item, int Count)> rewards = TableFields.S(row, 16).Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':')).Where(p => p.Length == 3)
                .ToDictionary(p => uint.Parse(p[0]), p => (uint.Parse(p[1]), int.Parse(p[2])));
            List<(uint, long, uint, int)> meals = [];
            foreach (string[] p in TableFields.S(row, 11).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split(':')).Where(p => p.Length == 3))
            {
                uint slot = uint.Parse(p[0]);
                long opens = new DateTimeOffset(chinaToday.AddHours(int.Parse(p[1])).AddMinutes(int.Parse(p[2])), TimeSpan.Zero).AddHours(-8).ToUnixTimeSeconds();
                (uint item, int count) = rewards.GetValueOrDefault(slot);
                meals.Add((slot, opens, item, count));
            }

            return (row, meals);
        }

        private static string FoodKey => "activity.dailyfood";

        private Dictionary<string, bool> FoodTaken(uint uid) => accountState.Get<Dictionary<string, bool>>(uid, FoodKey) ?? [];

        private static string FoodSlotKey(uint slot) => $"{GameDay.Key()}:{slot}";

        public override void HandleGetActivityDailyFood(CS_GetActivityDailyFood request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_GetActivityDailyFood reply = new();
            if (DailyFood() is { } food)
            {
                Dictionary<string, bool> taken = FoodTaken(connection.Account.Uid);
                foreach ((uint slot, long opens, _, _) in food.Meals)
                    reply.PLAKNGGJGPL[slot] = new JMNBIFKFMLB { ENGFMGEEMBG = opens, State = taken.GetValueOrDefault(FoodSlotKey(slot)) };
            }
            connection.Send(reply);
        }

        public override void HandleTakeActivityDailyFood(CS_TakeActivityDailyFood request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<IMessage> messages = [];
            SC_TakeActivityDailyFood reply = new();
            if (DailyFood() is { } food)
            {
                lock (AccountLock.For(uid))
                {
                    Dictionary<string, bool> taken = FoodTaken(uid);
                    // id 0 takes every meal already served today
                    foreach ((uint slot, long opens, uint item, int count) in food.Meals.Where(m => request.Id == 0 || m.Slot == request.Id))
                    {
                        if (now < opens || taken.GetValueOrDefault(FoodSlotKey(slot)))
                            continue;
                        if (item != 0 && count > 0)
                            rewardService.Grant(uid, item, count, messages);
                        taken[FoodSlotKey(slot)] = true;
                        reply.AJNAPNNKIFC[slot] = true;
                    }
                    accountState.Set(uid, FoodKey, taken);
                }
            }

            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGetActivityConversion(CS_GetActivityConversion request, Connection connection)
        {
                    connection.Send(new SC_GetActivityConversion
                    {
                        GIOIKFICGAE = new PKPJMDCFFKD
                        {
                            CurrId = 0,
                            FPAEHEGGAIH = false,
                        },
                    });
        }

        public override void HandleActivitySkinBargainInfo(CS_ActivitySkinBargainInfo request, Connection connection)
        {
                    connection.Send(new SC_ActivitySkinBargainInfo
                    {
                        Info = { },
                    });
        }

        public override void HandleActivityLotteryInfo(CS_ActivityLotteryInfo request, Connection connection)
        {
                    connection.Send(new SC_ActivityLotteryInfo()
                    {
                        BNKEKDKPCIE = { }
                    });
        }

        public override void HandleActivityBackOneTimeReward(CS_ActivityBackOneTimeReward request, Connection connection)
        {
            // "Welcome back" popup shown at login (SC_ActivityBackDailyRefresh). The client waits for this (empty) reply before
            // leaving the popup. The official reward is not known, so nothing is granted (no reward duplication either).
            Serilog.Log.Information("ActivityBackOneTimeReward event {EventId} acknowledged; no reward granted.", request.EventId);
            connection.Send(new SC_ActivityBackOneTimeReward());
        }

        public override void HandleActivityBackCheckIn(CS_ActivityBackCheckIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            uint day = activityBack.CheckIn(connection.Account.Uid, messages);
            if (day == 0)
            {
                connection.Refuse(MsgId.MsgCsActivityBackCheckIn, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            messages.Add(new SC_ActivityBackCheckIn { IOLHEKBEICG = day });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>
        /// Delayed sign-in (KNDPEMBMJOB, GIGOLKKBPDJ.DelaySignIn): an event sign-in (EventSigninData / EventSigninRewardData of the event)
        /// whose state is field 2 = {today done, days claimed}; the client counts it finished when the days reach the reward rows.
        /// </summary>
        public override void HandleActivityDelaySignInGetInfo(CS_ActivityDelaySignInGetInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            (bool done, uint days) = eventSignin.Progress(connection.Account.Uid, request.IKDMNOBMIFE);
            connection.Send(new SC_ActivityDelaySignInGetInfo
            {
                IKDMNOBMIFE = request.IKDMNOBMIFE,
                NLPAKPNKNHL = new NHIEEFMCMHC { CheckinDone = done, CheckinDay = days },
            });
        }

        public override void HandleActivityDelaySignIn(CS_ActivityDelaySignIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            eventSignin.TryClaim(connection.Account.Uid, request.IKDMNOBMIFE, messages);
            (bool done, uint days) = eventSignin.Progress(connection.Account.Uid, request.IKDMNOBMIFE);
            messages.Add(new SC_ActivityDelaySignIn
            {
                IKDMNOBMIFE = request.IKDMNOBMIFE,
                NLPAKPNKNHL = new NHIEEFMCMHC { CheckinDone = done, CheckinDay = days },
            });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Cake event state per event (DCKOKKEEGAL): stages claimed, the blessing (name or wish) and the final draw.</summary>
        public sealed class CakeState
        {
            public List<uint> Claimed { get; set; } = [];
            public bool Blessed { get; set; }
            public string Name { get; set; } = string.Empty;
            public uint Wish { get; set; }
            public bool Drawn { get; set; }
        }

        private static string CakeKey(uint activityId) => $"activity.cake.{activityId}";

        private SC_ActivityCakeGetInfo CakeInfo(uint uid, uint activityId)
        {
            CakeState state = accountState.Get<CakeState>(uid, CakeKey(activityId)) ?? new CakeState();
            AJNHOCPMNCO info = new() { CGJPFJPKDOL = state.Blessed, JKNFABAKBHF = state.Name, WishId = state.Wish, PCMFEJAIHKF = state.Drawn };
            info.EKDFIHOEBJD.AddRange(state.Claimed);
            return new SC_ActivityCakeGetInfo { IKDMNOBMIFE = activityId, DJAIAHACABH = info };
        }

        public override void HandleActivityCakeGetInfo(CS_ActivityCakeGetInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(CakeInfo(connection.Account.Uid, request.IKDMNOBMIFE));
        }

        /// <summary>
        /// A stage of the cake (CakeShowData: 2 = event, 3 = stage 1..5, 4/5 = its window, 6 = reward). The client sends the stage row id
        /// (field 2) and, for the blessing, field 6 = true with the wish (field 5, CakeBlessData) or a free name (field 3).
        /// </summary>
        public override void HandleActivityCakeClaimReward(CS_ActivityCakeClaimReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                CakeState state = accountState.Get<CakeState>(uid, CakeKey(request.IKDMNOBMIFE)) ?? new CakeState();
                CakeShowData? stage = tableService.GetTable<CakeShowData>().FirstOrDefault(r => r.Id == request.LEPBLEICAID && r.Group == request.IKDMNOBMIFE);
                if (stage is null)
                {
                    connection.Refuse(MsgId.MsgCsActivityCakeClaimReward, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                if (request.LFIPENCGNOD)
                {
                    state.Blessed = true;
                    state.Wish = request.WishId;
                    state.Name = request.WishId == 0 ? request.JKNFABAKBHF ?? string.Empty : string.Empty;
                }
                else if (!state.Claimed.Contains(stage.Id))
                {
                    if (stage.Reward is { } reward)
                        for (int i = 0; i < Math.Min(reward.BAALJFJDGBB.Count, reward.Value.Count); i++)
                            rewardService.Grant(uid, reward.BAALJFJDGBB[i], (int)reward.Value[i], messages);
                    state.Claimed.Add(stage.Id);
                }

                accountState.Set(uid, CakeKey(request.IKDMNOBMIFE), state);
            }

            messages.Add(new SC_ActivityCakeClaimReward { IKDMNOBMIFE = request.IKDMNOBMIFE, LEPBLEICAID = request.LEPBLEICAID });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>The final draw of the cake (stage 5); the client only needs the flag back.</summary>
        public override void HandleActivityCakeGacha(CS_ActivityCakeGacha request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                CakeState state = accountState.Get<CakeState>(uid, CakeKey(request.IKDMNOBMIFE)) ?? new CakeState();
                state.Drawn = true;
                accountState.Set(uid, CakeKey(request.IKDMNOBMIFE), state);
            }

            connection.Send(new SC_ActivityCakeGacha { IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        /// <summary>
        /// World boss (GAPBIEOMKJB). On a local server the shared boss is fought by this account alone. ASSUMPTION A-190: SLGInfo field 1 is
        /// the chosen doll, 4 the best score, 5 the total score, 2 the claimed rewards per type (LDMNMDGCMJO: 1 share, 2 voucher, 3 total
        /// score); Raid field 3 is the score of the fight. Rewards come from EventWorldbossDpsScoreRewardsData (per fight, column 3 = score
        /// needed), EventWorldbossScoreRewardsData (total score) and EventWorldbossVoucherRewardsData; the boss HP from
        /// EventWorldbossBaseData column 11.
        /// </summary>
        public sealed class WorldBossState
        {
            public uint Gun { get; set; }
            public uint Best { get; set; }
            public uint Total { get; set; }
            public ulong Damage { get; set; }
            public uint Tickets { get; set; }
            public long SelectedAt { get; set; }
            public Dictionary<uint, List<uint>> Claimed { get; set; } = [];
        }

        private static string BossKey(uint activityId) => $"activity.worldboss.{activityId}";

        private WorldBossState Boss(uint uid, uint activityId) => accountState.Get<WorldBossState>(uid, BossKey(activityId)) ?? new WorldBossState();

        private ulong BossHp(WorldBossState state)
        {
            ulong max = tableService.GetTable<EventWorldbossBaseData>().Select(r => TableFields.U64(r, 11)).DefaultIfEmpty(10_000_000_000UL).Max();
            return state.Damage >= max ? 0 : max - state.Damage;
        }

        private static SLGInfo BossInfo(WorldBossState state)
        {
            SLGInfo info = new() { NAPFGHGNACL = state.Gun, CGFMBCCDFPG = state.Best, DEGNALHNOMI = state.Total, OLPDKLAOKHJ = state.Tickets, KIGFMADBIHH = state.SelectedAt };
            foreach ((uint type, List<uint> ids) in state.Claimed)
            {
                MBIKJFJEFAH claimed = new() { BBBFCHOOHHL = ids.Count > 0 };
                foreach (uint id in ids)
                    claimed.IMPANHBNFJF[id] = true;
                info.Rewards[type] = claimed;
            }

            return info;
        }

        public override void HandleActivityWorldBossInfo(CS_ActivityWorldBossInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            WorldBossState state = Boss(connection.Account.Uid, request.IKDMNOBMIFE);
            connection.Send(new SC_ActivityWorldBossInfo { IKDMNOBMIFE = request.IKDMNOBMIFE, COGKOIJLDOC = BossInfo(state), CHBFHIPAOIE = BossHp(state) });
        }

        public override void HandleActivityWorldBossRemainHp(CS_ActivityWorldBossRemainHp request, Connection connection)
        {
            if (connection.Account is null)
                return;
            connection.Send(new SC_ActivityWorldBossRemainHp { IKDMNOBMIFE = request.IKDMNOBMIFE, CHBFHIPAOIE = BossHp(Boss(connection.Account.Uid, request.IKDMNOBMIFE)) });
        }

        public override void HandleActivityWorldBossSelectGun(CS_ActivityWorldBossSelectGun request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (AccountLock.For(uid))
            {
                WorldBossState state = Boss(uid, request.IKDMNOBMIFE);
                state.Gun = request.NAPFGHGNACL;
                state.SelectedAt = now;
                accountState.Set(uid, BossKey(request.IKDMNOBMIFE), state);
            }

            connection.Send(new SC_ActivityWorldBossSelectGun { IKDMNOBMIFE = request.IKDMNOBMIFE, NAPFGHGNACL = request.NAPFGHGNACL, KIGFMADBIHH = now });
        }

        public override void HandleActivityWorldBossRaid(CS_ActivityWorldBossRaid request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            SC_ActivityWorldBossRaid reply = new();
            lock (AccountLock.For(uid))
            {
                WorldBossState state = Boss(uid, request.IKDMNOBMIFE);
                uint score = request.IIJGACGAEAO;
                state.Best = Math.Max(state.Best, score);
                state.Total += score;
                state.Damage += score;
                // the reward of the best grade reached by this fight
                EventWorldbossDpsScoreRewardsData? grade = tableService.GetTable<EventWorldbossDpsScoreRewardsData>()
                    .Where(r => r.FOBNJHJFHNG <= score).OrderByDescending(r => r.FOBNJHJFHNG).FirstOrDefault();
                if (grade is not null)
                    foreach ((uint item, uint count) in grade.Rewards)
                    {
                        rewardService.Grant(uid, item, (int)count, messages);
                        reply.Rewards[item] = reply.Rewards.GetValueOrDefault(item) + count;
                    }
                reply.DEGNALHNOMI = state.Total;
                accountState.Set(uid, BossKey(request.IKDMNOBMIFE), state);
            }

            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleActivityWorldBossGetReward(CS_ActivityWorldBossGetReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            SC_ActivityWorldBossGetReward reply = new() { IKDMNOBMIFE = request.IKDMNOBMIFE, RewardType = request.RewardType };
            lock (AccountLock.For(uid))
            {
                WorldBossState state = Boss(uid, request.IKDMNOBMIFE);
                uint type = (uint)request.RewardType;
                List<uint> claimed = state.Claimed.GetValueOrDefault(type) ?? [];
                foreach (uint id in request.Ids.Distinct())
                {
                    if (claimed.Contains(id))
                        continue;
                    IEnumerable<KeyValuePair<uint, uint>>? rewards = request.RewardType == LDMNMDGCMJO.TypeVoucher
                        ? tableService.GetTable<EventWorldbossVoucherRewardsData>().FirstOrDefault(r => r.Id == id)?.Rewards
                        : tableService.GetTable<EventWorldbossScoreRewardsData>().FirstOrDefault(r => r.Id == id && r.FOBNJHJFHNG <= state.Total)?.Rewards;
                    if (rewards is null)
                        continue;
                    foreach ((uint item, uint count) in rewards)
                        rewardService.Grant(uid, item, (int)count, messages);
                    claimed.Add(id);
                    reply.Ids.Add(id);
                }

                state.Claimed[type] = claimed;
                accountState.Set(uid, BossKey(request.IKDMNOBMIFE), state);
            }

            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleActivityWorldBossPayTickets(CS_ActivityWorldBossPayTickets request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                WorldBossState state = Boss(uid, request.IKDMNOBMIFE);
                state.Tickets += request.CNOGAEPCBFJ;
                accountState.Set(uid, BossKey(request.IKDMNOBMIFE), state);
            }

            connection.Send(new SC_ActivityWorldBossPayTickets { IKDMNOBMIFE = request.IKDMNOBMIFE, CNOGAEPCBFJ = request.CNOGAEPCBFJ });
        }

        public override void HandleActivityWorldBossCampRank(CS_ActivityWorldBossCampRank request, Connection connection)
        {
            if (connection.Account is null)
                return;
            // a local server has a single player: first of every ranking
            connection.Send(new SC_ActivityWorldBossCampRank { EPCCANNLACO = request.EPCCANNLACO, GunId = request.GunId, NGAEHDBAEDP = 1, Total = 1 });
        }

        public override void HandleActivityWorldBossVoucher(CS_ActivityWorldBossVoucher request, Connection connection)
        {
            if (connection.Account is null)
                return;
            connection.Send(new SC_ActivityWorldBossVoucher { IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        public override void HandleActivityWorldBossVoucherBackup(CS_ActivityWorldBossVoucherBackup request, Connection connection)
        {
            if (connection.Account is null)
                return;
            connection.Send(new SC_ActivityWorldBossVoucherBackup { IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        /// <summary>
        /// Character rerun (LMCPGHPAGCA): the state is one flag, "the opening reward was taken" (the red dot of the event stays until then).
        /// Its tasks run through the quest system (TaskListData of the event).
        /// </summary>
        private static string RerunKey(uint activityId) => $"activity.rerun.{activityId}";

        public override void HandleActivityGetCharacterRerunInfo(CS_ActivityGetCharacterRerunInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            bool taken = accountState.Get<bool?>(connection.Account.Uid, RerunKey(request.IKDMNOBMIFE)) ?? false;
            connection.Send(new SC_ActivityGetCharacterRerunInfo { PEDIIDPJFKI = new LGLIHGGBLHI { BHHGOBKHEHF = taken } });
        }

        public override void HandleActivityCharacterRerunGetReward(CS_ActivityCharacterRerunGetReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            accountState.Set(connection.Account.Uid, RerunKey(request.IKDMNOBMIFE), (bool?)true);
            connection.Send(new SC_ActivityCharacterRerunGetReward { IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        private ActivityBoxGacha BoxGacha => new(accountState, tableService, rewardService, itemUse);

        public override void HandleGetActivityGacha(CS_GetActivityGacha request, Connection connection) => BoxGacha.HandleGetActivityGacha(request, connection);

        public override void HandleActivityGacha(CS_ActivityGacha request, Connection connection) => BoxGacha.HandleActivityGacha(request, connection);

        public override void HandleActivityGachaSettle(CS_ActivityGachaSettle request, Connection connection) => BoxGacha.HandleActivityGachaSettle(request, connection);

        /// <summary>One-time and daily claims of small events, kept per account under "activity.claims".</summary>
        public sealed class ClaimState
        {
            public List<string> Done { get; set; } = [];
            public Dictionary<uint, uint> AnniversaryDays { get; set; } = [];
            public Dictionary<uint, string> AnniversaryLast { get; set; } = [];
        }

        private const string ClaimKey = "activity.claims";

        private void Grant(uint uid, IEnumerable<KeyValuePair<uint, uint>> items, List<IMessage> messages)
        {
            foreach ((uint item, uint count) in items)
                if (item != 0 && count > 0)
                    rewardService.Grant(uid, item, (int)count, messages);
        }

        /// <summary>Reward of a "new system" event (EventNewSystemMainData column 3), once.</summary>
        public override void HandleActivityNewSystemReward(CS_ActivityNewSystemReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                ClaimState state = accountState.Get<ClaimState>(uid, ClaimKey) ?? new ClaimState();
                string key = $"newsystem:{request.IKDMNOBMIFE}";
                if (!state.Done.Contains(key) && tableService.GetTable<EventNewSystemMainData>().FirstOrDefault(r => TableFields.U(r, 1) == request.IKDMNOBMIFE) is { } row)
                {
                    Grant(uid, TableFields.Map(row, 3).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                    state.Done.Add(key);
                    accountState.Set(uid, ClaimKey, state);
                }
            }

            long end = tableService.GetTable<ActivityListData>().FirstOrDefault(r => r.Id == request.IKDMNOBMIFE)?.CloseTime ?? 0;
            messages.Add(new SC_ActivityNewSystemReward { IKDMNOBMIFE = request.IKDMNOBMIFE, EndTime = end });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>
        /// Anniversary sign-in (EventSigninAnniversaryData: 4 = reward group, 6 = final bonus): one day a game day, the reward of that day
        /// from EventSigninAnniversaryRewardsData (2 = group, 3 = day, 4 = reward).
        /// </summary>
        public override void HandleActivityCheckInAnniversary(CS_ActivityCheckInAnniversary request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                ClaimState state = accountState.Get<ClaimState>(uid, ClaimKey) ?? new ClaimState();
                Google.Protobuf.IMessage? config = tableService.GetTable<EventSigninAnniversaryData>().FirstOrDefault(r => TableFields.U(r, 1) == request.EventId);
                string today = GameDay.Key();
                if (config is not null && state.AnniversaryLast.GetValueOrDefault(request.EventId) != today)
                {
                    uint group = TableFields.U(config, 4);
                    uint day = state.AnniversaryDays.GetValueOrDefault(request.EventId) + 1;
                    if (tableService.GetTable<EventSigninAnniversaryRewardsData>().FirstOrDefault(r => TableFields.U(r, 2) == group && TableFields.U(r, 3) == day) is { } reward)
                    {
                        Grant(uid, TableFields.Map(reward, 4).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                        state.AnniversaryDays[request.EventId] = day;
                        state.AnniversaryLast[request.EventId] = today;
                        accountState.Set(uid, ClaimKey, state);
                    }
                }
            }

            messages.Add(new SC_ActivityCheckInAnniversary { EventId = request.EventId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>The final bonus of the anniversary sign-in (EventSigninAnniversaryData column 6), once.</summary>
        public override void HandleActivityAnniversaryClaimBonus(CS_ActivityAnniversaryClaimBonus request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                ClaimState state = accountState.Get<ClaimState>(uid, ClaimKey) ?? new ClaimState();
                string key = $"anniversarybonus:{request.EventId}";
                if (!state.Done.Contains(key) && tableService.GetTable<EventSigninAnniversaryData>().FirstOrDefault(r => TableFields.U(r, 1) == request.EventId) is { } config)
                {
                    Grant(uid, TableFields.Map(config, 6).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                    state.Done.Add(key);
                    accountState.Set(uid, ClaimKey, state);
                }
            }

            messages.Add(new SC_ActivityAnniversaryClaimBonus { EventId = request.EventId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleActivityCheckInMessageOver(CS_ActivityCheckInMessageOver request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_ActivityCheckInMessageOver { EventId = request.EventId });
        }

        /// <summary>
        /// 3D wish board (ActivityThreeDimensionsAmo): AmoActivityMainclass3dData (1 = event, 3 = its boards, 8 = final reward) and
        /// AmoActivitySubclass3dData (1 = board, 10 = its reward). The state is, per board, the nodes lit and whether its reward was
        /// taken (field 2 = final reward taken). ASSUMPTION A-197: the map key and the reward id of the requests are the board id; id 0
        /// asks for the final reward, given once every board was rewarded.
        /// </summary>
        public sealed class Amo3dState
        {
            public Dictionary<uint, List<uint>> Nodes { get; set; } = [];
            public List<uint> Rewarded { get; set; } = [];
            public bool Final { get; set; }
        }

        private static string Amo3dKey(uint activityId) => $"activity.amo3d.{activityId}";

        private SC_ActivityThreeDimensionsAmoGetInfo Amo3dInfo(uint activityId, Amo3dState state)
        {
            JOBMDFAEAEE info = new() { LFDGEHMLOHM = state.Final };
            IReadOnlyList<uint> boards = tableService.GetTable<AmoActivityMainclass3dData>().FirstOrDefault(r => TableFields.U(r, 1) == activityId) is { } main
                ? TableFields.List(main, 3) : [];
            foreach (uint board in boards.Union(state.Nodes.Keys))
            {
                JOBMDFAEAEE.Types.FFNGBLOKAAG entry = new() { Reward = state.Rewarded.Contains(board) };
                entry.ICBFKPLLCLF.AddRange(state.Nodes.GetValueOrDefault(board) ?? []);
                info.ICGIMHODGIE[board] = entry;
            }

            return new SC_ActivityThreeDimensionsAmoGetInfo { IKDMNOBMIFE = activityId, OFLLFNJOIOA = info };
        }

        public void HandleActivityThreeDimensionsAmoGetInfo(CS_ActivityThreeDimensionsAmoGetInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(Amo3dInfo(request.IKDMNOBMIFE, accountState.Get<Amo3dState>(connection.Account.Uid, Amo3dKey(request.IKDMNOBMIFE)) ?? new Amo3dState()));
        }

        public override void HandleActivityThreeDimensionsAmoNodeCommit(CS_ActivityThreeDimensionsAmoNodeCommit request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                Amo3dState state = accountState.Get<Amo3dState>(uid, Amo3dKey(request.IKDMNOBMIFE)) ?? new Amo3dState();
                List<uint> nodes = state.Nodes.GetValueOrDefault(request.OMIHLNBPGHG) ?? [];
                if (!nodes.Contains(request.JBEONCNOEEA))
                    nodes.Add(request.JBEONCNOEEA);
                state.Nodes[request.OMIHLNBPGHG] = nodes;
                accountState.Set(uid, Amo3dKey(request.IKDMNOBMIFE), state);
            }

            connection.Send(new SC_ActivityThreeDimensionsAmoNodeCommit { IKDMNOBMIFE = request.IKDMNOBMIFE, OMIHLNBPGHG = request.OMIHLNBPGHG, JBEONCNOEEA = request.JBEONCNOEEA });
        }

        public void ClaimAmo3dFinal(uint uid, uint activityId, out List<IMessage> messages)
        {
            messages = [];
            lock (AccountLock.For(uid))
            {
                Amo3dState state = accountState.Get<Amo3dState>(uid, Amo3dKey(activityId)) ?? new Amo3dState();
                if (!state.Final && tableService.GetTable<AmoActivityMainclass3dData>().FirstOrDefault(r => TableFields.U(r, 1) == activityId) is { } main)
                {
                    Grant(uid, TableFields.Map(main, 8).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                    state.Final = true;
                    accountState.Set(uid, Amo3dKey(activityId), state);
                }
            }
        }

        public void HandleActivityThreeDimensionsAmoGetReward(CS_ActivityThreeDimensionsAmoGetReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                Amo3dState state = accountState.Get<Amo3dState>(uid, Amo3dKey(request.IKDMNOBMIFE)) ?? new Amo3dState();
                if (request.KNMAPOCKMCJ == 0)
                {
                    if (!state.Final && tableService.GetTable<AmoActivityMainclass3dData>().FirstOrDefault(r => TableFields.U(r, 1) == request.IKDMNOBMIFE) is { } main)
                    {
                        Grant(uid, TableFields.Map(main, 8).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                        state.Final = true;
                    }
                }
                else if (!state.Rewarded.Contains(request.KNMAPOCKMCJ)
                    && tableService.GetTable<AmoActivitySubclass3dData>().FirstOrDefault(r => TableFields.U(r, 1) == request.KNMAPOCKMCJ) is { } board)
                {
                    Grant(uid, TableFields.Map(board, 10).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                    state.Rewarded.Add(request.KNMAPOCKMCJ);
                }

                accountState.Set(uid, Amo3dKey(request.IKDMNOBMIFE), state);
            }

            messages.Add(new SC_ActivityThreeDimensionsAmoGetReward { IKDMNOBMIFE = request.IKDMNOBMIFE, KNMAPOCKMCJ = request.KNMAPOCKMCJ });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>
        /// Team pass gift (BattlepassTeamData: 1 = event, 3 = normal reward, 4 = extra reward of a paid pass). ASSUMPTION A-198: like the
        /// other passes of a private server the paid tier is owned, so the extra reward is the one offered; taken once.
        /// </summary>
        private static string BpTeamKey(uint activityId) => $"bpteam:{activityId}";

        public override void HandleActivityBpTeamInfo(CS_ActivityBpTeamInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            ClaimState state = accountState.Get<ClaimState>(connection.Account.Uid, ClaimKey) ?? new ClaimState();
            connection.Send(new SC_ActivityBpTeamInfo
            {
                Info = new POPPBFFKECA
                {
                    IKDMNOBMIFE = request.IKDMNOBMIFE,
                    Level = POPPBFFKECA.Types.EMPCJMBGLCB.RewardLevelExtra,
                    Status = state.Done.Contains(BpTeamKey(request.IKDMNOBMIFE)) ? POPPBFFKECA.Types.IKBAMNLCLPP.RewardStatusGot : POPPBFFKECA.Types.IKBAMNLCLPP.RewardStatusNone,
                },
            });
        }

        public override void HandleActivityBpTeamPickReward(CS_ActivityBpTeamPickReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                ClaimState state = accountState.Get<ClaimState>(uid, ClaimKey) ?? new ClaimState();
                if (!state.Done.Contains(BpTeamKey(request.IKDMNOBMIFE))
                    && tableService.GetTable<BattlepassTeamData>().FirstOrDefault(r => TableFields.U(r, 1) == request.IKDMNOBMIFE) is { } row)
                {
                    Grant(uid, TableFields.Map(row, 4).Select(p => new KeyValuePair<uint, uint>(p.Key, p.Value)), messages);
                    state.Done.Add(BpTeamKey(request.IKDMNOBMIFE));
                    accountState.Set(uid, ClaimKey, state);
                }
            }

            messages.Add(new SC_ActivityBpTeamPickReward { ICJPKOJLLFD = request.Clone() });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleActivityCheckIn(CS_ActivityCheckIn request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (!eventSignin.TryClaim(connection.Account.Uid, request.EventId, messages))
                return;

            messages.Add(new SC_ActivityCheckIn { EventId = request.EventId });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}

