using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed class DishFlags
    {
        public bool Claimed { get; set; }
        public bool Favorite { get; set; }
    }

    public sealed class BagDish
    {
        public uint Dish { get; set; }
        public uint Rank { get; set; }
        public uint Buff { get; set; }
        public uint Count { get; set; }
    }

    public sealed class CookState
    {
        public uint Proficiency { get; set; }
        /// <summary>Dishes cooked at least once (= unlocked in the illustrated book).</summary>
        public Dictionary<uint, DishFlags> Dishes { get; set; } = [];
        /// <summary>Milestone group → highest claimed level.</summary>
        public Dictionary<uint, uint> Milestones { get; set; } = [];
        public List<BagDish> Bag { get; set; } = [];
        public long DinnerDay { get; set; } = -1;
        public uint DinnerType { get; set; }
        public uint DinnerGun { get; set; }
        public long CoffeeDay { get; set; } = -1;
        public uint CoffeeBuff { get; set; }
        /// <summary>Dinner buff group -> level (missing = level 1) and coffee kind -> level (missing = level 1).</summary>
        public Dictionary<uint, uint> DinnerLevels { get; set; } = [];
        public Dictionary<uint, uint> CoffeeLevels { get; set; } = [];
        /// <summary>"gun:primary:sub" → satiety experience of that property.</summary>
        public Dictionary<string, uint> Satiety { get; set; } = [];
    }

    /// <summary>
    /// Lobby kitchen and café: dish cooking, the dish bag, gifts, the illustrated book, the daily dinner buff and the daily coffee.
    /// Contracts are those of client 4.0.5136 (ids 29500-29519, 29105-29108, 29131-29133); rules come from the client tables
    /// read by field number and from its own texts. Nothing of this is confirmed by a real client or by the official server.
    /// <para><b>Read from tables/texts</b>: a recipe is the ingredient multiset of <c>LobbyCookWildcardData</c> (2 → dish 3); a
    /// combination without recipe fails and the ingredients are lost (text 105982); every dish cooked gives +1 proficiency (texts
    /// 98499, 98586); <c>LobbyCookProficiencyData</c> 2 = proficiency needed per level; the book rewards are
    /// <c>LobbyCookIllustrationsData</c> 12 per dish and <c>LobbyCookIllustrationsLevelData</c> (3 level, 6 dishes needed, 7 reward);
    /// affection gifts use <c>LobbyCookLoveData</c> (rank → points), property gifts <c>LobbyCookAttrData</c> (dish → (primary, sub),
    /// rank → satiety) with the levels of <c>LobbyCookPropertyData</c> (5 = experience of the level); coffee rows are
    /// <c>LobbyCafeBuffData</c> (2 = kind, 3 = 1 generic, 11 = level).</para>
    /// <para><b>ASSUMPTIONS</b>: dish ranks are 4 (success) and 5 (great success, the only ranks the tables define); the chance of
    /// a great success is column 4 of <c>LobbyCookProficiencyData</c> in percent (0 below level 4); the character only has to be
    /// owned; the buff (third key of a dish) is always 0; a failed cooking gives no proficiency; the third field of
    /// <c>SC_LobbyDishCook</c> means "first time this dish is cooked"; satiety levels cost 10/15/25 each (column 5, not cumulative
    /// in the table); property gifts of different (primary, sub) cannot be mixed; the property u64 sent is the total satiety;
    /// the dinner is free once a day, extra ones cost <c>LobbyConfigData</c> column 20 (1610000 × 20); only dinner groups 1-9 and 15
    /// at level 1 exist (the group levels the server has always announced); only the generic coffee kinds 1-6 and 67 at level 1
    /// exist and one can be made per day (day changes at 21:00 UTC). Ingredients have no source on this server (use
    /// <c>/inventory</c>). The buffs themselves (extra loot, dinner effect) are applied by other systems, not here.</para>
    /// </summary>
    public interface ILobbyCookingService : IGameService
    {
        /// <summary>Dinner buff groups the account owns (all at level 1), as announced by the captured <c>SC_LobbyBuffInfo</c>.</summary>
        static readonly uint[] DinnerGroups = [1, 2, 3, 4, 5, 6, 7, 8, 9, 15];

        /// <summary>Coffee kinds the account owns (level 1), as announced by the captured <c>SC_LobbyGetCafeInfo</c>.</summary>
        static readonly uint[] CoffeeKinds = [1, 2, 3, 4, 5, 6, 67];

        EOBBIJAPFPD Info(uint uid);

        Activities Bag(uint uid);

        uint Proficiency(uint uid);

        /// <summary>Active dinner buff of today: (group, level, free dinner already used).</summary>
        (uint Group, uint Level, bool Used) Dinner(uint uid);

        /// <summary>Row of <c>LobbyCafeBuffData</c> of today's coffee (0 = none).</summary>
        uint CoffeeBuff(uint uid);

        HNINDAOJCKP StartCook(uint uid, uint group, uint gun, List<IMessage> messages);

        HNINDAOJCKP Cook(uint uid, IReadOnlyList<uint> ingredients, uint gun, uint wildcardId, List<IMessage> messages);

        HNINDAOJCKP Gift(uint uid, CS_LobbyDishGift request, List<IMessage> messages);

        /// <summary>The PropertyData rows the dishes given to a character switched on (the row of the level each property reached), A-76.</summary>
        IReadOnlyList<uint> FoodRows(uint uid, uint gunId);

        /// <summary>The bytes the client keeps in <c>Gun</c> field 61: the points of each property (slots 0-2 = primary 1 with sub 0-2, slot 7 = the most fed primary 2).</summary>
        ulong FoodBytes(uint uid, uint gunId);

        HNINDAOJCKP ClaimIndex(uint uid, IReadOnlyList<uint> dishes, List<IMessage> messages);

        HNINDAOJCKP ClaimMilestone(uint uid, uint group, List<IMessage> messages, out uint level);

        HNINDAOJCKP Favorite(uint uid, uint dish, out bool favorite);

        HNINDAOJCKP MakeCoffee(uint uid, uint kind, out uint buff);

        /// <summary>Level of every dinner buff group / coffee kind the account owns.</summary>
        IReadOnlyDictionary<uint, uint> DinnerLevels(uint uid);

        IReadOnlyDictionary<uint, uint> CoffeeLevels(uint uid);

        /// <summary>
        /// Raises a dinner buff group / coffee kind by one level, paying the cost of the target row (<c>LobbyCookBuffData</c> col 13 text
        /// "item:amount" for the dinner, <c>LobbyCafeBuffData</c> col 12 map for the coffee; level = col 6 / col 11).
        /// </summary>
        HNINDAOJCKP UpgradeDinner(uint uid, uint group, List<IMessage> messages, out uint level);

        HNINDAOJCKP UpgradeCoffee(uint uid, uint kind, List<IMessage> messages, out uint level);
    }

    public sealed class LobbyCookingService(
        IAccountStateService accountState,
        ITableService tableService,
        IAccountService accountService,
        IGunService gunService,
        IItemUseService itemUse,
        IRewardService rewardService,
        ILoveService love) : ILobbyCookingService
    {
        private const string StateKey = "lobby.cook";
        private const uint IngredientType = 173;
        private const uint StackLimit = 999;
        private const uint ProficiencyBonusRank = 5;
        private static readonly object Gate = new();

        // ----- reads -----

        public EOBBIJAPFPD Info(uint uid)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                EOBBIJAPFPD.Types.CKLGALBLAPO group = new() { CJMDEFFJFLM = state.Milestones.GetValueOrDefault(1u) };
                foreach ((uint dish, DishFlags flags) in state.Dishes)
                    group.GMEAMJPGLBH[dish] = new NBCAutoBattle { NHOAAJOECCK = flags.Claimed, BGJNHNFFEKK = flags.Favorite };

                EOBBIJAPFPD info = new() { CLLCFMCMONP = state.Proficiency };
                info.DJCLEPBDFJD[1] = group;
                return info;
            }
        }

        public Activities Bag(uint uid)
        {
            lock (Gate)
            {
                Activities bag = new();
                foreach (BagDish entry in Load(uid).Bag.Where(b => b.Count > 0))
                {
                    if (!bag.DABKAEIBBGA.TryGetValue(entry.Dish, out Activities.Types.LoungeChatRoom? ranks))
                        bag.DABKAEIBBGA[entry.Dish] = ranks = new Activities.Types.LoungeChatRoom();
                    if (!ranks.HNGELNIBCMN.TryGetValue(entry.Rank, out Activities.Types.DOEMBLOEHML? buffs))
                        ranks.HNGELNIBCMN[entry.Rank] = buffs = new Activities.Types.DOEMBLOEHML();
                    buffs.PJNFOIALIFH[entry.Buff] = entry.Count;
                }

                return bag;
            }
        }

        public uint Proficiency(uint uid)
        {
            lock (Gate)
                return Load(uid).Proficiency;
        }

        public (uint Group, uint Level, bool Used) Dinner(uint uid)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                bool today = state.DinnerDay == Day(Now());
                return today ? (state.DinnerType, state.DinnerLevels.GetValueOrDefault(state.DinnerType, 1u), true) : (0u, 0u, false);
            }
        }

        public uint CoffeeBuff(uint uid)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                return state.CoffeeDay == Day(Now()) ? state.CoffeeBuff : 0;
            }
        }

        // ----- daily dinner ("old cook") -----

        public HNINDAOJCKP StartCook(uint uid, uint group, uint gun, List<IMessage> messages)
        {
            lock (Gate)
            {
                if (!ILobbyCookingService.DinnerGroups.Contains(group)
                    || !tableService.GetTable<LobbyCookBuffData>().Any(r => TableFields.U(r, 3) == group && TableFields.U(r, 6) == 1))
                    return HNINDAOJCKP.LobbyFoodBuffLocked;

                if (!Owns(uid, gun))
                    return HNINDAOJCKP.GunNotFound;

                CookState state = Load(uid);
                long today = Day(Now());
                if (state.DinnerDay == today)
                {
                    // ASSUMPTION: a second dinner of the day replaces the first and costs the configured price.
                    if (tableService.GetTable<LobbyConfigData>().FirstOrDefault() is not { } config
                        || TableFields.Map(config, 20).FirstOrDefault() is not { Key: > 0 } price)
                        return HNINDAOJCKP.ItemNotEnough;

                    if (!itemUse.Spend(uid, new Dictionary<uint, int> { [price.Key] = (int)price.Value }, messages))
                        return HNINDAOJCKP.ItemNotEnough;
                }

                state.DinnerDay = today;
                state.DinnerType = group;
                state.DinnerGun = gun;
                Save(uid, state);
                Log.Information("Cooking: account {Uid} had dinner of group {Group} with character {Gun}.", uid, group, gun);
                return HNINDAOJCKP.None;
            }
        }

        // ----- dishes -----

        public HNINDAOJCKP Cook(uint uid, IReadOnlyList<uint> ingredients, uint gun, uint wildcardId, List<IMessage> messages)
        {
            lock (Gate)
            {
                if (ingredients.Count == 0)
                    return HNINDAOJCKP.ClientInputInvalid;

                if (!Owns(uid, gun))
                    return HNINDAOJCKP.GunNotFound;

                IReadOnlyList<ItemData> items = tableService.GetTable<ItemData>().ToList();
                foreach (uint ingredient in ingredients)
                    if (items.FirstOrDefault(i => i.Id == ingredient) is not { } row || row.Type != IngredientType)
                        return HNINDAOJCKP.ItemConfigNotExist;

                uint[] sorted = ingredients.Order().ToArray();
                IMessage? recipe = tableService.GetTable<LobbyCookWildcardData>()
                    .FirstOrDefault(r => TableFields.List(r, 2).Order().SequenceEqual(sorted));
                uint dish = recipe is null ? 0 : TableFields.U(recipe, 3);
                if (dish != 0 && !tableService.GetTable<LobbyCookIllustrationsData>().Any(d => TableFields.U(d, 1) == dish))
                    dish = 0;

                if (wildcardId != 0 && recipe is not null && TableFields.U(recipe, 1) != wildcardId)
                    Log.Warning("Cooking: the client sent recipe row {Sent} but the ingredients match row {Found}; using the latter.", wildcardId, TableFields.U(recipe, 1));

                CookState state = Load(uid);
                uint rank = 0;
                if (dish != 0)
                {
                    uint chance = GreatChance(state.Proficiency);
                    bool great = Environment.GetEnvironmentVariable("GRIFFIN_COOK_FORCE_GREAT") == "1" || Random.Shared.Next(100) < chance;
                    rank = great ? ProficiencyBonusRank : 4;
                    if (state.Bag.FirstOrDefault(b => b.Dish == dish && b.Rank == rank && b.Buff == 0) is { } stack && stack.Count >= StackLimit)
                        return HNINDAOJCKP.LobbyDishBagLimit;
                }

                Dictionary<uint, int> cost = [];
                foreach (uint ingredient in ingredients)
                    cost[ingredient] = cost.GetValueOrDefault(ingredient) + 1;
                if (!itemUse.Spend(uid, cost, messages))
                    return HNINDAOJCKP.ItemNotEnough;

                if (dish == 0)
                {
                    Log.Information("Cooking: account {Uid} wasted {Count} ingredient(s) on a combination without recipe.", uid, ingredients.Count);
                    messages.Add(new SC_LobbyDishCook { Result = SC_LobbyDishCook.Types.ICIFJHKELNP.Fail });
                    return HNINDAOJCKP.None;
                }

                bool first = !state.Dishes.ContainsKey(dish);
                if (first)
                    state.Dishes[dish] = new DishFlags();
                BagDish entry = AddToBag(state, dish, rank, 0, 1);
                state.Proficiency++;
                Save(uid, state);

                messages.Add(new SC_UpdateDishNum { ItemId = dish, Rank = rank, AEOAALPABHP = 0, Num = entry.Count });
                messages.Add(new SC_LobbyCookingProficiency { DHNFIEFDMCD = state.Proficiency });
                messages.Add(new SC_LobbyDishCook
                {
                    Result = rank == ProficiencyBonusRank ? SC_LobbyDishCook.Types.ICIFJHKELNP.GreatSuccess : SC_LobbyDishCook.Types.ICIFJHKELNP.Success,
                    CGFMCGJBBKB = new HADFNHKDJCB { ItemId = dish, Rank = rank, AEOAALPABHP = 0 },
                    NFDNIGCBNEF = first,
                });
                Log.Information("Cooking: account {Uid} cooked dish {Dish} rank {Rank} (proficiency {Proficiency}).", uid, dish, rank, state.Proficiency);
                return HNINDAOJCKP.None;
            }
        }

        /// <summary>The level of a property for a satiety total: the highest <c>LobbyCookPropertyData</c> row whose accumulated experience (column 5) it reaches.</summary>
        private IMessage? LevelRow(uint primary, uint sub, uint total)
        {
            IMessage? best = null;
            uint accumulated = 0;
            foreach (IMessage row in tableService.GetTable<LobbyCookPropertyData>().Where(r => TableFields.U(r, 2) == primary && TableFields.U(r, 3) == sub).OrderBy(r => TableFields.U(r, 4)))
            {
                accumulated += TableFields.U(row, 5);
                if (total >= accumulated)
                    best = row;
            }

            return best;
        }

        public IReadOnlyList<uint> FoodRows(uint uid, uint gunId)
        {
            List<uint> rows = [];
            foreach ((string key, uint total) in Load(uid).Satiety)
            {
                string[] parts = key.Split(':');
                if (parts.Length == 3 && uint.TryParse(parts[0], out uint gun) && gun == gunId && uint.TryParse(parts[1], out uint primary) && uint.TryParse(parts[2], out uint sub)
                    && LevelRow(primary, sub, total) is { } row)
                    rows.Add(TableFields.U(row, 6));
            }

            return rows;
        }

        public ulong FoodBytes(uint uid, uint gunId) => FoodBytesOf(Load(uid), gunId);

        private static ulong FoodBytesOf(CookState state, uint gunId)
        {
            ulong bytes = 0;
            uint bestElement = 0;
            foreach ((string key, uint total) in state.Satiety)
            {
                string[] parts = key.Split(':');
                if (parts.Length != 3 || !uint.TryParse(parts[0], out uint gun) || gun != gunId || !uint.TryParse(parts[1], out uint primary) || !uint.TryParse(parts[2], out uint sub))
                    continue;

                ulong value = Math.Min(255u, total);
                if (primary == 1 && sub <= 2)
                    bytes |= value << (int)(sub * 8);
                else if (primary == 2 && total > bestElement)
                {
                    bestElement = total;
                    bytes = (bytes & ~(0xFFUL << 56)) | (value << 56);
                }
            }

            return bytes;
        }

        public HNINDAOJCKP Gift(uint uid, CS_LobbyDishGift request, List<IMessage> messages)
        {
            lock (Gate)
            {
                if (request.LIMCAHKNPFJ.Count == 0 || request.LIMCAHKNPFJ.Any(i => i.Num == 0))
                    return HNINDAOJCKP.ClientInputInvalid;

                GunEntity? gun = accountService.GetByUid(uid)?.Guns.FirstOrDefault(g => g.GunId == request.GunId);
                if (gun is null)
                    return HNINDAOJCKP.GunNotFound;

                CookState state = Load(uid);
                Dictionary<(uint Dish, uint Rank, uint Buff), uint> wanted = [];
                foreach (var item in request.LIMCAHKNPFJ)
                {
                    var key = (item.ItemId, item.Rank, item.AEOAALPABHP);
                    wanted[key] = wanted.GetValueOrDefault(key) + item.Num;
                }

                foreach (((uint dish, uint rank, uint buff), uint num) in wanted)
                    if ((state.Bag.FirstOrDefault(b => b.Dish == dish && b.Rank == rank && b.Buff == buff)?.Count ?? 0) < num)
                        return HNINDAOJCKP.ItemNotEnough;

                List<IMessage> result = [];
                if (request.Type == IKMBDMBBGMH.Love)
                {
                    long gained = 0;
                    foreach (((uint dish, uint rank, uint _), uint num) in wanted)
                    {
                        IMessage? row = tableService.GetTable<LobbyCookLoveData>().FirstOrDefault(r => TableFields.U(r, 1) == dish);
                        uint points = row is null ? 0 : TableFields.Map(row, 2).FirstOrDefault(p => p.Key == rank).Value;
                        if (points == 0)
                            return HNINDAOJCKP.ClientInputInvalid;
                        gained += (long)points * num;
                    }

                    if (gun.LoveLevel >= LoveCap() && gun.LoveExp >= LoveThreshold(LoveCap()))
                        return HNINDAOJCKP.LoveLevelMax;

                    // the bar of the level is full and its event pending (A-55): no more experience until it is done
                    if (love.Pending(gun) is not null)
                        return HNINDAOJCKP.LoveExpOverflow;

                    Take(state, wanted, result);
                    love.AddExperience(gun, gained);
                    gunService.Save();
                    result.Add(new SC_LoveLevelUpdate { GunId = gun.GunId, JLHFICPMAEI = gun.LoveLevel, FDKIHAPHHPD = love.Pending(gun)?.Suffix ?? 0 });
                    Log.Information("Cooking: account {Uid} gave dishes to {Gun}: +{Gained} affection.", uid, gun.GunId, gained);
                }
                else
                {
                    (uint Primary, uint Sub)? target = null;
                    uint gain = 0;
                    foreach (((uint dish, uint rank, uint _), uint num) in wanted)
                    {
                        IMessage? row = tableService.GetTable<LobbyCookAttrData>().FirstOrDefault(r => TableFields.U(r, 1) == dish);
                        uint satiety = row is null ? 0 : TableFields.Map(row, 4).FirstOrDefault(p => p.Key == rank).Value;
                        if (row is null || satiety == 0)
                            return HNINDAOJCKP.ClientInputInvalid;

                        (uint, uint) property = (TableFields.U(row, 2), TableFields.U(row, 3));
                        if (target is not null && target != property)
                            return HNINDAOJCKP.LobbyDishCanNotGiftTogether;
                        target = property;
                        gain += satiety * num;
                    }

                    (uint primary, uint sub) = target!.Value;
                    IReadOnlyList<IMessage> levels = tableService.GetTable<LobbyCookPropertyData>()
                        .Where(r => TableFields.U(r, 2) == primary && TableFields.U(r, 3) == sub)
                        .OrderBy(r => TableFields.U(r, 4)).ToList();
                    if (levels.Count == 0)
                        return HNINDAOJCKP.ClientInputInvalid;

                    uint max = (uint)levels.Sum(r => TableFields.U(r, 5));
                    string key = $"{gun.GunId}:{primary}:{sub}";
                    uint current = state.Satiety.GetValueOrDefault(key);
                    if (current >= max)
                        return HNINDAOJCKP.LobbyDishExpMax;

                    Take(state, wanted, result);
                    uint total = Math.Min(max, current + gain);
                    state.Satiety[key] = total;

                    uint level = 0, accumulated = 0;
                    IMessage? levelRow = null;
                    foreach (IMessage row in levels)
                    {
                        accumulated += TableFields.U(row, 5);
                        if (total >= accumulated)
                        {
                            level = TableFields.U(row, 4);
                            levelRow = row;
                        }
                    }

                    result.Add(new SC_GunDishPropertyExpUp
                    {
                        GunId = gun.GunId,
                        FKONCECGFFM = primary,
                        LOJOMOMJLPB = sub,
                        EMOLDIANHCJ = level,
                        JKGBIKEDACK = levelRow is null ? 0 : PropertyValue(TableFields.U(levelRow, 6)),
                        EEPPFPLKGKN = FoodBytesOf(state, gun.GunId),
                    });
                    Log.Information("Cooking: account {Uid} gave dishes to {Gun}: property {Primary}/{Sub} satiety {Total}, level {Level}.", uid, gun.GunId, primary, sub, total, level);
                }

                Save(uid, state);
                messages.AddRange(result);
                return HNINDAOJCKP.None;
            }
        }

        // ----- illustrated book -----

        public HNINDAOJCKP ClaimIndex(uint uid, IReadOnlyList<uint> dishes, List<IMessage> messages)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                if (dishes.Count == 0)
                    return HNINDAOJCKP.ClientInputInvalid;

                foreach (uint dish in dishes.Distinct())
                {
                    if (!state.Dishes.TryGetValue(dish, out DishFlags? flags))
                        return HNINDAOJCKP.LobbyDishIndexUnlock;
                    if (flags.Claimed)
                        return HNINDAOJCKP.LobbyDishIndexClaimed;
                }

                foreach (uint dish in dishes.Distinct())
                {
                    state.Dishes[dish].Claimed = true;
                    IMessage? row = tableService.GetTable<LobbyCookIllustrationsData>().FirstOrDefault(r => TableFields.U(r, 1) == dish);
                    foreach ((uint item, int count) in rewardService.ParseRewards(row is null ? null : TableFields.S(row, 12)))
                        rewardService.Grant(uid, item, count, messages);
                }

                Save(uid, state);
                return HNINDAOJCKP.None;
            }
        }

        public HNINDAOJCKP ClaimMilestone(uint uid, uint group, List<IMessage> messages, out uint level)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                uint current = state.Milestones.GetValueOrDefault(group);
                level = current;
                IMessage? row = tableService.GetTable<LobbyCookIllustrationsLevelData>()
                    .FirstOrDefault(r => TableFields.U(r, 2) == group && TableFields.U(r, 3) == current + 1);
                if (row is null || state.Dishes.Count < TableFields.U(row, 6))
                    return HNINDAOJCKP.LobbyDishIndexMilestoneCantUp;

                foreach ((uint item, int count) in rewardService.ParseRewards(TableFields.S(row, 7)))
                    rewardService.Grant(uid, item, count, messages);

                level = current + 1;
                state.Milestones[group] = level;
                Save(uid, state);
                return HNINDAOJCKP.None;
            }
        }

        public HNINDAOJCKP Favorite(uint uid, uint dish, out bool favorite)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                favorite = false;
                if (!state.Dishes.TryGetValue(dish, out DishFlags? flags))
                    return HNINDAOJCKP.LobbyDishIndexUnlock;

                flags.Favorite = !flags.Favorite;
                favorite = flags.Favorite;
                Save(uid, state);
                return HNINDAOJCKP.None;
            }
        }

        // ----- coffee -----

        public HNINDAOJCKP MakeCoffee(uint uid, uint kind, out uint buff)
        {
            lock (Gate)
            {
                buff = 0;
                IMessage? row = ILobbyCookingService.CoffeeKinds.Contains(kind)
                    ? tableService.GetTable<LobbyCafeBuffData>().FirstOrDefault(r => TableFields.U(r, 2) == kind && TableFields.U(r, 3) == 1
                        && TableFields.U(r, 11) == Load(uid).CoffeeLevels.GetValueOrDefault(kind, 1u))
                    : null;
                if (row is null)
                    return HNINDAOJCKP.LobbyCoffeeLocked;

                CookState state = Load(uid);
                long today = Day(Now());
                if (state.CoffeeDay == today)
                    return HNINDAOJCKP.LobbyCoffeeBuffAlreadyExist;

                state.CoffeeDay = today;
                state.CoffeeBuff = buff = TableFields.U(row, 1);
                Save(uid, state);
                Log.Information("Cooking: account {Uid} made coffee kind {Kind} (buff row {Buff}).", uid, kind, buff);
                return HNINDAOJCKP.None;
            }
        }

        // ----- levels -----

        public IReadOnlyDictionary<uint, uint> DinnerLevels(uint uid)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                return ILobbyCookingService.DinnerGroups.ToDictionary(g => g, g => state.DinnerLevels.GetValueOrDefault(g, 1u));
            }
        }

        public IReadOnlyDictionary<uint, uint> CoffeeLevels(uint uid)
        {
            lock (Gate)
            {
                CookState state = Load(uid);
                return ILobbyCookingService.CoffeeKinds.ToDictionary(k => k, k => state.CoffeeLevels.GetValueOrDefault(k, 1u));
            }
        }

        public HNINDAOJCKP UpgradeDinner(uint uid, uint group, List<IMessage> messages, out uint level)
        {
            lock (Gate)
            {
                level = 0;
                if (!ILobbyCookingService.DinnerGroups.Contains(group))
                    return HNINDAOJCKP.ClientInputInvalid;

                CookState state = Load(uid);
                level = state.DinnerLevels.GetValueOrDefault(group, 1u);
                uint wanted = level + 1;
                IMessage? next = tableService.GetTable<LobbyCookBuffData>().FirstOrDefault(r => TableFields.U(r, 3) == group && TableFields.U(r, 6) == wanted);
                if (next is null)
                    return HNINDAOJCKP.LobbyFoodBuffLevelMax;

                Dictionary<uint, int> cost = [];
                foreach (string part in TableFields.S(next, 13).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] pair = part.Split(':');
                    if (pair.Length == 2 && uint.TryParse(pair[0], out uint item) && int.TryParse(pair[1], out int count) && count > 0)
                        cost[item] = cost.GetValueOrDefault(item) + count;
                }

                if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                    return HNINDAOJCKP.ItemNotEnough;

                state.DinnerLevels[group] = ++level;
                Save(uid, state);
                Log.Information("Cooking: account {Uid} raised dinner buff group {Group} to level {Level}.", uid, group, level);
                return HNINDAOJCKP.None;
            }
        }

        public HNINDAOJCKP UpgradeCoffee(uint uid, uint kind, List<IMessage> messages, out uint level)
        {
            lock (Gate)
            {
                level = 0;
                if (!ILobbyCookingService.CoffeeKinds.Contains(kind))
                    return HNINDAOJCKP.LobbyCoffeeLocked;

                CookState state = Load(uid);
                level = state.CoffeeLevels.GetValueOrDefault(kind, 1u);
                uint wanted = level + 1;
                IMessage? next = tableService.GetTable<LobbyCafeBuffData>().FirstOrDefault(r => TableFields.U(r, 2) == kind && TableFields.U(r, 3) == 1 && TableFields.U(r, 11) == wanted);
                if (next is null)
                    return HNINDAOJCKP.LobbyCafeBuffLevelMax;

                Dictionary<uint, int> cost = TableFields.Map(next, 12).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
                if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                    return HNINDAOJCKP.ItemNotEnough;

                state.CoffeeLevels[kind] = ++level;
                Save(uid, state);
                Log.Information("Cooking: account {Uid} raised coffee kind {Kind} to level {Level}.", uid, kind, level);
                return HNINDAOJCKP.None;
            }
        }

        // ----- helpers -----

        private static BagDish AddToBag(CookState state, uint dish, uint rank, uint buff, uint count)
        {
            BagDish? entry = state.Bag.FirstOrDefault(b => b.Dish == dish && b.Rank == rank && b.Buff == buff);
            if (entry is null)
                state.Bag.Add(entry = new BagDish { Dish = dish, Rank = rank, Buff = buff });
            entry.Count += count;
            return entry;
        }

        private static void Take(CookState state, Dictionary<(uint Dish, uint Rank, uint Buff), uint> wanted, List<IMessage> messages)
        {
            foreach (((uint dish, uint rank, uint buff), uint num) in wanted)
            {
                BagDish entry = state.Bag.First(b => b.Dish == dish && b.Rank == rank && b.Buff == buff);
                entry.Count -= num;
                messages.Add(new SC_UpdateDishNum { ItemId = dish, Rank = rank, AEOAALPABHP = buff, Num = entry.Count });
            }

            state.Bag.RemoveAll(b => b.Count == 0);
        }

        private uint GreatChance(uint proficiency)
        {
            IMessage? level = tableService.GetTable<LobbyCookProficiencyData>()
                .Where(r => TableFields.U(r, 2) <= proficiency).OrderBy(r => TableFields.U(r, 3)).LastOrDefault();
            return level is null ? 0 : TableFields.U(level, 4);
        }

        /// <summary>The single numeric stat column of a <c>PropertyData</c> row (ASSUMPTION: its value is what the client prints).</summary>
        private uint PropertyValue(uint rowId)
        {
            IMessage? row = tableService.GetTable<PropertyData>().FirstOrDefault(r => TableFields.U(r, 1) == rowId);
            if (row is null)
                return 0;

            foreach (var field in row.Descriptor.Fields.InFieldNumberOrder().Where(f => f.FieldNumber > 1))
                if (field.Accessor.GetValue(row) is { } value && !field.IsRepeated && !field.IsMap && Convert.ToInt64(value is IMessage ? 0 : value) is var number and > 0)
                    return (uint)number;
            return 0;
        }

        private bool Owns(uint uid, uint gun) => gun != 0 && accountService.GetByUid(uid)?.Guns.Any(g => g.GunId == gun) == true;

        private uint LoveCap() => love.Cap;

        private long LoveThreshold(uint level) => love.Threshold(level);

        private CookState Load(uint uid) => accountState.Get<CookState>(uid, StateKey) ?? new CookState();

        private void Save(uint uid, CookState state) => accountState.Set(uid, StateKey, state);

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Day number; the day changes at 21:00 UTC (05:00 UTC+8).</summary>
        private static long Day(long time) => GameDay.Number(time);
    }
}
