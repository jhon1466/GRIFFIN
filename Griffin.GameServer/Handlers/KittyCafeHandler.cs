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
    /// Kitty cafe mini game, second edition (KittyCafe2, module 22003, e.g. theme 3401 -> Kittycafe2ConfigData 102), as read in the client
    /// 4.0.5142 data manager (EHGKOPBPJHF):
    /// - Info (CEPAACNIEFJ): 1 grade, 2 score towards the next grade (Kittycafe2GradeData column 6, cumulative), 3 owned cats, 4 daily earnings,
    ///   5 staff seat (Kittycafe2StaffData) -> cat, 6 handbook entries (is_collected false = found, true = reward claimed), 7 level -> finished
    ///   targets, 8 all-collection reward claimed, 9 cat -> last interaction time.
    /// - Cats are Kittycafe2AvatarData rows of type 2 (column 4) of the avatar group of the config (column 5); the cat dialog buys them for the
    ///   cost in column 16 (the client greys the button when the bag holds less). A new cat adds KittyCafeCatData column 2 to the score.
    /// - Levels (Kittycafe2LevelData: 2 group = config column 7, 6 previous level, 10 grade needed) are settled with the targets reached; each
    ///   new target (Kittycafe2TargetData column 2 = level) grants its column 14 rewards.
    /// - The daily shop converts money with the DailyExchangeRate (Kittycafe2ParamData column 7) into the token of the config (column 13); the
    ///   client caps one settle at column 9 and the day at column 8 minus the earnings of the day, which the server enforces too.
    /// - The all-collection reward is the config column 14 (a title).
    /// </summary>
    public sealed class KittyCafe2Handler(
        IAccountStateService accountState,
        ITableService tableService,
        IRewardService rewardService,
        IItemUseService itemUseService) : KittyCafe2HandlerBase
    {
        public sealed class State
        {
            public uint Grade { get; set; } = 1;
            public uint Score { get; set; }
            public List<uint> Cats { get; set; } = [];
            public Dictionary<uint, uint> Staff { get; set; } = [];
            public Dictionary<uint, bool> Collection { get; set; } = [];
            public Dictionary<uint, List<uint>> Levels { get; set; } = [];
            public bool AllCollectionClaimed { get; set; }
            public Dictionary<uint, long> CatTimes { get; set; } = [];
            public Dictionary<uint, uint> CatCounts { get; set; } = [];
            public long Day { get; set; }
            public uint DailyEarnings { get; set; }
        }

        private static string Key(uint theme) => $"kittycafe2.{theme}";

        private static long Today() => GameDay.Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        private State Load(uint uid, uint theme)
        {
            State state = accountState.Get<State>(uid, Key(theme)) ?? new State();
            long today = Today();
            if (state.Day != today)
            {
                // ASSUMPTION: the daily earnings (client cap Kittycafe2ParamData column 8) and the cat interaction counts restart every game day.
                state.Day = today;
                state.DailyEarnings = 0;
                state.CatCounts.Clear();
            }
            return state;
        }

        private void Save(uint uid, uint theme, State state) => accountState.Set(uid, Key(theme), state);

        private IMessage? Config(uint theme)
        {
            uint id = ThemeModules.Config(tableService, theme, ThemeModules.KittyCafe2);
            return id == 0 ? null : tableService.GetTable<Kittycafe2ConfigData>().FirstOrDefault(r => TableFields.U(r, 1) == id);
        }

        private IMessage? Param() => tableService.GetTable<Kittycafe2ParamData>().FirstOrDefault();

        private IMessage? Avatar(uint id) => tableService.GetTable<Kittycafe2AvatarData>().FirstOrDefault(r => TableFields.U(r, 1) == id);

        private List<IMessage> Grades(IMessage config) =>
            tableService.GetTable<Kittycafe2GradeData>().Where(r => TableFields.U(r, 2) == TableFields.U(config, 6)).Cast<IMessage>().OrderBy(r => TableFields.U(r, 3)).ToList();

        private CEPAACNIEFJ Info(State state)
        {
            CEPAACNIEFJ info = new()
            {
                CMADKJPJPHF = state.Grade,
                Score = state.Score,
                MGGMMKOLIIA = state.DailyEarnings,
                HLMKMBFENPN = state.AllCollectionClaimed,
            };
            info.PMGALPJCPOM.AddRange(state.Cats);
            info.JGDCACENMLA.Add(state.Staff);
            foreach ((uint id, bool claimed) in state.Collection)
                info.FMNALCJLCNF[id] = new PLDKOMOPOCO { IsCollected = claimed };
            foreach ((uint level, List<uint> targets) in state.Levels)
            {
                HEAIMKNLPLG record = new();
                record.EPLMNDJKKMP.AddRange(targets);
                info.LLKIPBOMFNC[level] = record;
            }
            info.MKMJPKEIBKE.Add(state.CatTimes);
            return info;
        }

        /// <summary>Grants rewards; the config exp item (column 12) also raises the grade score. Grades reached grant their column 9 rewards.</summary>
        private void Grant(uint uid, uint theme, IMessage config, State state, IEnumerable<(uint Item, uint Count)> rewards, List<IMessage> messages)
        {
            uint exp = TableFields.U(config, 12);
            foreach ((uint item, uint count) in rewards)
            {
                if (item == 0 || count == 0)
                    continue;
                rewardService.Grant(uid, item, (int)count, messages);
                // ASSUMPTION: the grade score is the Kittycafe2 exp item (config column 12, "Exp") earned in the cafe; the grade thresholds
                // (hundreds to thousands) are only reachable with the exp of the targets, the cats alone give 5 to 20.
                if (item == exp)
                    state.Score += count;
            }
            AddScore(uid, theme, config, state, 0, messages);
        }

        private void AddScore(uint uid, uint theme, IMessage config, State state, uint score, List<IMessage> messages)
        {
            state.Score += score;
            List<IMessage> grades = Grades(config);
            bool upgraded = false;
            while (true)
            {
                IMessage? next = grades.FirstOrDefault(g => TableFields.U(g, 3) == state.Grade + 1);
                if (next is null || state.Score < TableFields.U(next, 6))
                    break;
                state.Grade++;
                upgraded = true;
                foreach ((uint item, uint count) in TableFields.Map(next, 9))
                    if (item != 0 && count != 0)
                        rewardService.Grant(uid, item, (int)count, messages);
            }
            if (upgraded)
                messages.Add(new SC_KittyCafe2Upgrade { ThemeId = theme, CMADKJPJPHF = state.Grade, Score = state.Score });
        }

        public override void HandleKittyCafe2Info(CS_KittyCafe2Info request, Connection connection)
        {
            if (connection.Account is null)
                return;
            State state;
            lock (AccountLock.For(connection.Account.Uid))
            {
                state = Load(connection.Account.Uid, request.ThemeId);
                Save(connection.Account.Uid, request.ThemeId, state);
            }
            connection.Send(new SC_KittyCafe2Info { ThemeId = request.ThemeId, Info = Info(state) });
        }

        public override void HandleKittyCafe2UnlockCat(CS_KittyCafe2UnlockCat request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            IMessage? config = Config(request.ThemeId);
            IMessage? cat = Avatar(request.NPBMHHBMDOB);
            if (config is null || cat is null || TableFields.U(cat, 4) != 2 || TableFields.U(cat, 2) != TableFields.U(config, 5))
            {
                connection.Refuse(MsgId.MsgCsKittyCafe2UnlockCat, HNINDAOJCKP.KittyCafe2CatCfgNotFound);
                return;
            }

            using RewardDrops drops = RewardDrops.Begin();
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                if (state.Cats.Contains(request.NPBMHHBMDOB))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2UnlockCat, HNINDAOJCKP.KittyCafe2CatAlreadyBought);
                    return;
                }
                Dictionary<uint, int> cost = TableFields.Map(cat, 16).Where(p => p.Key != 0 && p.Value != 0).ToDictionary(p => p.Key, p => (int)p.Value);
                if (cost.Count > 0 && !itemUseService.Spend(uid, cost, messages))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2UnlockCat, HNINDAOJCKP.ItemNotEnough);
                    return;
                }
                state.Cats.Add(request.NPBMHHBMDOB);
                // a new cat is a handbook entry to claim (the client lights its red dot while is_collected is false)
                state.Collection.TryAdd(request.NPBMHHBMDOB, false);
                // the client adds KittyCafeCatData column 2 of the new cat to the score (LJCFNJPGLKL)
                uint score = tableService.GetTable<KittyCafeCatData>().Where(r => TableFields.U(r, 1) == request.NPBMHHBMDOB).Select(r => TableFields.U(r, 2)).FirstOrDefault();
                AddScore(uid, request.ThemeId, config, state, score, messages);
                Save(uid, request.ThemeId, state);
            }
            drops.Finish(messages);
            messages.Add(new SC_KittyCafe2UnlockCat { ThemeId = request.ThemeId, IAJBKCDNMHP = request.NPBMHHBMDOB });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleKittyCafe2SetStaff(CS_KittyCafe2SetStaff request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            Dictionary<uint, uint> staff;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                foreach ((uint seat, uint cat) in request.PHBAIKNKJHD)
                    if (cat != 0 && !state.Cats.Contains(cat))
                    {
                        connection.Refuse(MsgId.MsgCsKittyCafe2SetStaff, HNINDAOJCKP.KittyCafe2CatLocked);
                        return;
                    }
                // the request carries the whole arrangement (the client replaces its staff map with the reply)
                state.Staff = request.PHBAIKNKJHD.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value);
                staff = state.Staff;
                Save(uid, request.ThemeId, state);
            }
            SC_KittyCafe2SetStaff reply = new() { ThemeId = request.ThemeId };
            reply.PHBAIKNKJHD.Add(staff);
            connection.Send(reply);
        }

        public override void HandleKittyCafe2SettleLevel(CS_KittyCafe2SettleLevel request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            IMessage? config = Config(request.ThemeId);
            IMessage? level = tableService.GetTable<Kittycafe2LevelData>().FirstOrDefault(r => TableFields.U(r, 1) == request.MCKEKMMKFFA);
            if (config is null || level is null || TableFields.U(level, 2) != TableFields.U(config, 7))
            {
                connection.Refuse(MsgId.MsgCsKittyCafe2SettleLevel, HNINDAOJCKP.KittyCafe2LevelLocked);
                return;
            }

            using RewardDrops drops = RewardDrops.Begin();
            List<IMessage> messages = [];
            List<uint> finished;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                uint previous = TableFields.U(level, 6);
                if (previous != 0 && !state.Levels.ContainsKey(previous))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2SettleLevel, HNINDAOJCKP.KittyCafe2PreLevelLocked);
                    return;
                }
                if (state.Grade < TableFields.U(level, 10))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2SettleLevel, HNINDAOJCKP.KittyCafe2LevelLocked);
                    return;
                }
                List<IMessage> targets = tableService.GetTable<Kittycafe2TargetData>().Where(r => TableFields.U(r, 2) == request.MCKEKMMKFFA).Cast<IMessage>().ToList();
                if (request.GELKILFIPIJ.Any(t => targets.All(r => TableFields.U(r, 1) != t)))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2SettleLevel, HNINDAOJCKP.KittyCafe2LevelTargetInvalid);
                    return;
                }
                finished = state.Levels.GetValueOrDefault(request.MCKEKMMKFFA) ?? [];
                List<(uint, uint)> rewards = [];
                foreach (uint target in request.GELKILFIPIJ.Distinct())
                {
                    // the client only sends targets it has not finished yet; a repeated one grants nothing again
                    if (finished.Contains(target))
                        continue;
                    finished.Add(target);
                    rewards.AddRange(TableFields.Map(targets.First(r => TableFields.U(r, 1) == target), 14));
                }
                state.Levels[request.MCKEKMMKFFA] = finished;
                // ASSUMPTION: the customers served in the level (map keys) are handbook guests and the ids in their lists are recipes; ids
                // that are guest (Kittycafe2AvatarData of the config group) or recipe rows become handbook entries to claim.
                foreach ((uint guest, GADPAFDKNBH served) in request.OELCKIGPJMB)
                {
                    Discover(config, state, guest);
                    foreach (uint id in served.FCNPMFEBMJP.Concat(served.BOPKJOFBPJI))
                        Discover(config, state, id);
                }
                Grant(uid, request.ThemeId, config, state, rewards, messages);
                Save(uid, request.ThemeId, state);
            }
            drops.Finish(messages);
            SC_KittyCafe2SettleLevel reply = new() { ThemeId = request.ThemeId, MCKEKMMKFFA = request.MCKEKMMKFFA };
            reply.EPLMNDJKKMP.AddRange(finished);
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        private void Discover(IMessage config, State state, uint id)
        {
            if (id == 0 || state.Collection.ContainsKey(id))
                return;
            IMessage? avatar = Avatar(id);
            bool known = avatar is not null
                ? TableFields.U(avatar, 2) == TableFields.U(config, 5) && TableFields.U(avatar, 4) != 2
                : tableService.GetTable<Kittycafe2RecipeData>().Any(r => TableFields.U(r, 1) == id);
            if (known)
                state.Collection[id] = false;
        }

        public override void HandleKittyCafe2CollectionReward(CS_KittyCafe2CollectionReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            SC_KittyCafe2CollectionReward reply = new() { ThemeId = request.ThemeId };
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                foreach (uint id in request.BBLDGAPOFBO.Distinct())
                {
                    if (!state.Collection.TryGetValue(id, out bool claimed))
                    {
                        connection.Refuse(MsgId.MsgCsKittyCafe2CollectionReward, HNINDAOJCKP.KittyCafe2CollectionLocked);
                        return;
                    }
                    if (claimed)
                    {
                        connection.Refuse(MsgId.MsgCsKittyCafe2CollectionReward, HNINDAOJCKP.KittyCafe2CollectionAlreadyClaimed);
                        return;
                    }
                }
                // ASSUMPTION: no table lists an item for a single handbook entry; claiming only marks it (the client clears the red dot).
                foreach (uint id in request.BBLDGAPOFBO.Distinct())
                {
                    state.Collection[id] = true;
                    reply.OEPHLBHMBCM[id] = true;
                }
                Save(uid, request.ThemeId, state);
            }
            connection.Send(reply);
        }

        public override void HandleKittyCafe2AllCollectionReward(CS_KittyCafe2AllCollectionReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            IMessage? config = Config(request.ThemeId);
            if (config is null)
            {
                connection.Refuse(MsgId.MsgCsKittyCafe2AllCollectionReward, HNINDAOJCKP.KittyCafe2AllCollectionRewardLocked);
                return;
            }
            using RewardDrops drops = RewardDrops.Begin();
            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                if (state.AllCollectionClaimed)
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2AllCollectionReward, HNINDAOJCKP.KittyCafe2AllCollectionRewardClaimed);
                    return;
                }
                // every cat of the config owned and every handbook entry claimed (client LOBBJDGEKFH: nothing left to claim)
                uint group = TableFields.U(config, 5);
                bool allCats = tableService.GetTable<Kittycafe2AvatarData>()
                    .Where(r => TableFields.U(r, 4) == 2 && TableFields.U(r, 2) == group)
                    .All(r => state.Cats.Contains(TableFields.U(r, 1)));
                if (!allCats || state.Collection.Values.Any(c => !c))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2AllCollectionReward, HNINDAOJCKP.KittyCafe2AllCollectionRewardLocked);
                    return;
                }
                state.AllCollectionClaimed = true;
                Grant(uid, request.ThemeId, config, state, TableFields.Map(config, 14), messages);
                Save(uid, request.ThemeId, state);
            }
            drops.Finish(messages);
            messages.Add(new SC_KittyCafe2AllCollectionReward { ThemeId = request.ThemeId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleKittyCafe2SettleDailyLevel(CS_KittyCafe2SettleDailyLevel request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            IMessage? config = Config(request.ThemeId);
            IMessage? param = Param();
            if (config is null || param is null)
            {
                connection.Refuse(MsgId.MsgCsKittyCafe2SettleDailyLevel, HNINDAOJCKP.KittyCafe2DailyLevelEarningInvalid);
                return;
            }
            using RewardDrops drops = RewardDrops.Begin();
            List<IMessage> messages = [];
            uint daily;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                // same caps as the client (FGGAOJFOINC): one settle <= column 9, the day <= column 8
                uint perSettle = TableFields.U(param, 9);
                uint perDay = TableFields.U(param, 8);
                uint left = perDay > state.DailyEarnings ? perDay - state.DailyEarnings : 0;
                uint earnings = Math.Min(request.CAMJNNMNMMG, Math.Min(perSettle, left));
                state.DailyEarnings += earnings;
                daily = state.DailyEarnings;
                foreach (uint id in request.OELCKIGPJMB)
                    Discover(config, state, id);
                Grant(uid, request.ThemeId, config, state, [(TableFields.U(config, 13), earnings)], messages);
                Save(uid, request.ThemeId, state);
            }
            drops.Finish(messages);
            messages.Add(new SC_KittyCafe2DailyEarningsUpdate { ThemeId = request.ThemeId, DPHBHIEIOBJ = daily });
            messages.Add(new SC_KittyCafe2SettleDailyLevel { ThemeId = request.ThemeId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleKittyCafe2InterfaceWithCat(CS_KittyCafe2InterfaceWithCat request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            IMessage? config = Config(request.ThemeId);
            IMessage? param = Param();
            IMessage? cat = Avatar(request.NPBMHHBMDOB);
            if (config is null || param is null || cat is null)
            {
                connection.Refuse(MsgId.MsgCsKittyCafe2InterfaceWithCat, HNINDAOJCKP.KittyCafe2CatCfgNotFound);
                return;
            }
            if (TableFields.U(cat, 4) != 2)
            {
                connection.Refuse(MsgId.MsgCsKittyCafe2InterfaceWithCat, HNINDAOJCKP.KittyCafe2NotCat);
                return;
            }
            using RewardDrops drops = RewardDrops.Begin();
            List<IMessage> messages = [];
            SC_KittyCafe2InterfaceWithCat reply = new() { ThemeId = request.ThemeId, NPBMHHBMDOB = request.NPBMHHBMDOB };
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                if (!state.Cats.Contains(request.NPBMHHBMDOB))
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2InterfaceWithCat, HNINDAOJCKP.KittyCafe2CatLocked);
                    return;
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // ASSUMPTION: Kittycafe2ParamData columns 13 (30), 14 (3) and 15 (5), which the client never reads, are the server rules of
                // the paid cat interaction: cooldown in seconds, interactions per cat per day and tokens (config column 13) per interaction.
                uint cooldown = TableFields.U(param, 13);
                uint perDay = TableFields.U(param, 14);
                uint tokens = TableFields.U(param, 15);
                if (state.CatTimes.TryGetValue(request.NPBMHHBMDOB, out long last) && now - last < cooldown)
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2InterfaceWithCat, HNINDAOJCKP.KittyCafe2CatInteractInCd);
                    return;
                }
                uint count = state.CatCounts.GetValueOrDefault(request.NPBMHHBMDOB);
                if (perDay != 0 && count >= perDay)
                {
                    connection.Refuse(MsgId.MsgCsKittyCafe2InterfaceWithCat, HNINDAOJCKP.KittyCafe2CatInteractCountLimited);
                    return;
                }
                state.CatTimes[request.NPBMHHBMDOB] = now;
                state.CatCounts[request.NPBMHHBMDOB] = count + 1;
                uint token = TableFields.U(config, 13);
                if (token != 0 && tokens != 0)
                {
                    Grant(uid, request.ThemeId, config, state, [(token, tokens)], messages);
                    reply.Rewards[token] = tokens;
                }
                reply.INIGCPPONHP = now;
                Save(uid, request.ThemeId, state);
            }
            drops.Finish(messages);
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }

    /// <summary>
    /// First kitty cafe (KittyCafe, module 22001, e.g. theme 1401). Client 4.0.5142 has no data manager for it any more (only the KittyCafe2
    /// replies are registered); its MakeCoffee request is still sent by the KittyCafe2 cafe when a customer is served. The state is kept so
    /// that the info shows what was done: NPJAMJCBFHJ 1 grade, 3 cats, 5 staff.
    /// ASSUMPTION: without client logic to follow, the first edition grants nothing and only records cats and staff.
    /// </summary>
    public sealed class KittyCafeHandler(IAccountStateService accountState) : KittyCafeHandlerBase
    {
        public sealed class State
        {
            public List<uint> Cats { get; set; } = [];
            public List<uint> Staff { get; set; } = [];
        }

        private static string Key(uint theme) => $"kittycafe.{theme}";

        public override void HandleKittyCafeInfo(CS_KittyCafeInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            State state = accountState.Get<State>(connection.Account.Uid, Key(request.ThemeId)) ?? new State();
            NPJAMJCBFHJ info = new() { CMADKJPJPHF = 1 };
            info.PMGALPJCPOM.AddRange(state.Cats);
            info.BCMIJAJBJDP.AddRange(state.Staff);
            connection.Send(new SC_KittyCafeInfo { ThemeId = request.ThemeId, Info = info });
        }

        public override void HandleKittyCafeMakeCoffee(CS_KittyCafeMakeCoffee request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_KittyCafeMakeCoffee { ThemeId = request.ThemeId, NpcId = request.NpcId });
        }

        public override void HandleKittyCafeUnlockCat(CS_KittyCafeUnlockCat request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = accountState.Get<State>(uid, Key(request.ThemeId)) ?? new State();
                if (!state.Cats.Contains(request.NPBMHHBMDOB))
                    state.Cats.Add(request.NPBMHHBMDOB);
                accountState.Set(uid, Key(request.ThemeId), state);
            }
            connection.Send(new SC_KittyCafeUnlockCat { ThemeId = request.ThemeId, IAJBKCDNMHP = request.NPBMHHBMDOB });
        }

        public override void HandleKittyCafeSetStaff(CS_KittyCafeSetStaff request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = accountState.Get<State>(uid, Key(request.ThemeId)) ?? new State();
                state.Staff = request.JGDCACENMLA.ToList();
                accountState.Set(uid, Key(request.ThemeId), state);
            }
            SC_KittyCafeSetStaff reply = new() { ThemeId = request.ThemeId };
            reply.JGDCACENMLA.AddRange(request.JGDCACENMLA);
            connection.Send(reply);
        }
    }
}
