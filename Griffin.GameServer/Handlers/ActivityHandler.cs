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

        public override void HandleGetActivityDailyFood(CS_GetActivityDailyFood request, Connection connection)
        {
            // The installed 4.0.5136 tables contain no daily-food schedule. Returning
            // old captured timestamps would advertise already-expired claims.
            connection.Send(new SC_GetActivityDailyFood());
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

