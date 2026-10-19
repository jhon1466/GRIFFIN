using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// 遗珍回收 mode "Rouge 1" (plans 600002 and 600003, seasons 2 and 3): preparation (difficulty, trend, opening chip, squad), the map of nodes,
    /// battle nodes and the settlement. Phase 1 (docs/rouge-plan.md): the battle nodes are real; the gun node works; the event, supply and shop nodes
    /// only let the player pass (no reward, shop without goods) and no chip is offered after a battle. Every column is read by field number from the
    /// client tables; the numbers and the open points are in docs/assumptions.md (A-116 onwards).
    /// </summary>
    public interface IRougeService : IGameService
    {
        bool IsPlan(uint plan);

        HNAJJFJEJGO BuildInfo(uint uid, uint plan);

        PLPBPIBDGNB? GetMap(uint uid, uint plan);

        /// <summary>Stores what the player chose so far; returns the error to refuse the request with, or null.</summary>
        HNINDAOJCKP? Prepare(uint uid, CS_RougePrepare request);

        void CancelPrepare(uint uid, uint plan);

        HNINDAOJCKP? Start(uint uid, uint plan, out PLPBPIBDGNB? map, List<Google.Protobuf.IMessage> messages);

        HNINDAOJCKP? OpenNode(uint uid, uint plan, uint nodeId, out ODFMONHCBIO? node, out PJDHPAHKGJL? gunPack);

        HNINDAOJCKP? PreviewNode(uint uid, uint plan, uint nodeId, out ODFMONHCBIO? node);

        HNINDAOJCKP? InteractNode(uint uid, uint plan, uint nodeId, uint param1, uint param2, out ODFMONHCBIO? node, List<Google.Protobuf.IMessage> messages);

        /// <summary>Pick / give up / refresh of the chip or character pack; <paramref name="messages"/> receives the whole reply (the pack message first).</summary>
        HNINDAOJCKP? PackOp(uint uid, uint plan, CS_RougePackOp.Types.AAOPFMBDJHO op, CS_RougePackOp.Types.JAJGPMKINOD target, uint id, List<Google.Protobuf.IMessage> messages);

        /// <summary>Collects the rewards of the level table (RougeGradeData) the level of the account reached.</summary>
        HNINDAOJCKP? TakeGradeReward(uint uid, uint plan, IReadOnlyCollection<uint> ids, List<Google.Protobuf.IMessage> messages, out List<uint> taken);

        /// <summary>Activates a talent of the tree of the season: checks the prerequisites, charges the cost and stores it; returns the full list of active talents.</summary>
        HNINDAOJCKP? UnlockTalent(uint uid, uint plan, uint talentId, List<Google.Protobuf.IMessage> messages, out List<uint> unlocked);

        /// <summary>The settlement of the run: clears the map and the preparation and adds the experience.</summary>
        HNINDAOJCKP? Finish(uint uid, uint plan, CDPCJHMOCPM reason, out SC_RougeFinish? result, List<Google.Protobuf.IMessage> messages);

        /// <summary>A battle node is about to be fought: checks it and returns the stage the node holds.</summary>
        HNINDAOJCKP? BeginBattle(uint uid, uint plan, uint nodeId, uint stageId);

        /// <summary>The squad of the run as (gun, preset grade, GunPresetData id).</summary>
        IReadOnlyList<RougeService.TeamGun> Team(uint uid, uint plan);

        /// <summary>The battle of <paramref name="nodeId"/> ended; the node is finished when it was won. Returns the node (null if there is no run).</summary>
        ODFMONHCBIO? EndBattle(uint uid, uint plan, uint nodeId, bool won, List<Google.Protobuf.IMessage> messages);
    }

    public sealed class RougeService(ITableService tables, IAccountStateService accountState, IGunService gunService, IItemUseService itemUse,
        IItemService itemService, IRewardService rewardService) : IRougeService
    {
        internal const uint FirstPlan = 600002;
        internal const uint LastPlan = 600003;
        private const int TeamSize = 4;
        private const int OptionCount = 3;

        public sealed record TeamGun(uint GunId, uint Grade, uint PresetId, uint Level);

        public sealed class RougeState
        {
            public uint Difficulty { get; set; }
            public uint Trend { get; set; }
            public uint Chip { get; set; }
            public List<uint> ChipOptions { get; set; } = [];
            public List<uint> Guns { get; set; } = [];
            public List<uint> Cleared { get; set; } = [];
            public List<uint> Collection { get; set; } = [];
            public uint Exp { get; set; }
            public string? Map { get; set; }
            public List<uint> GunOptions { get; set; } = [];
            public long Started { get; set; }
            public List<uint> Talents { get; set; } = [];
            public uint GunRefreshUsed { get; set; }
            public uint ChipRefreshUsed { get; set; }
            public List<uint> GradeTaken { get; set; } = [];
        }

        public bool IsPlan(uint plan) => plan is FirstPlan or LastPlan;

        private static uint SeasonOf(uint plan) => plan - 600000;

        private static string Key(uint plan) => $"rouge.state.{plan}";

        private RougeState Load(uint uid, uint plan) => accountState.Get<RougeState>(uid, Key(plan)) ?? new RougeState();

        private void Save(uint uid, uint plan, RougeState state) => accountState.Set(uid, Key(plan), state);

        // ---------------------------------------------------------------- tables

        private IEnumerable<T> Rows<T>() where T : IMessage<T>, new() => tables.GetTable<T>();

        private static uint Pick(IReadOnlyList<(uint Value, uint Weight)> options)
        {
            if (options.Count == 0)
                return 0;

            uint total = (uint)options.Sum(o => Math.Max(1u, o.Weight));
            uint roll = (uint)Random.Shared.NextInt64(0, Math.Max(1, total));
            foreach ((uint value, uint weight) in options)
            {
                uint w = Math.Max(1u, weight);
                if (roll < w)
                    return value;
                roll -= w;
            }

            return options[^1].Value;
        }

        /// <summary>The three trends (<c>RougeOpeningSelectionData</c> col 2 = season) offered by a season.</summary>
        private List<uint> Trends(uint season) =>
            Rows<RougeOpeningSelectionData>().Where(row => TableFields.U(row, 2) == season).OrderBy(row => row.Id).Select(row => row.Id).ToList();

        /// <summary>The levels (<c>RougeDifficultyData</c> col 3) of a season, in order.</summary>
        private List<RougeDifficultyData> Difficulties(uint season) =>
            Rows<RougeDifficultyData>().Where(row => TableFields.U(row, 2) == season).OrderBy(row => TableFields.U(row, 3)).ToList();

        private uint OwnedGrade(uint uid, uint gunId) =>
            gunService.GetPlayerGuns(uid).FirstOrDefault(g => g.GunId == gunId)?.Grade ?? 0;

        /// <summary>
        /// Port of the client's <c>KEKCFFIJAAE.AOFGLLCKEPG(gun)</c>: the preset of a character is the one of the grade the player has; with a lower grade
        /// the next higher one, and with a grade above all presets the highest. Null when the character has no preset.
        /// </summary>
        public TeamGun? PresetOf(uint uid, uint gunId)
        {
            RougePresetByGunIdData? link = Rows<RougePresetByGunIdData>().FirstOrDefault(row => row.GunId == gunId);
            if (link is null)
                return null;

            Dictionary<uint, RougePresetData> presets = Rows<RougePresetData>().ToDictionary(row => row.Id);
            List<(uint Grade, uint PresetId)> grades = link.Id
                .Where(presets.ContainsKey)
                .Select(id => (Grade: TableFields.U(presets[id], 3), PresetId: TableFields.U(presets[id], 4)))
                .OrderBy(entry => entry.Grade)
                .ToList();
            if (grades.Count == 0)
                return null;

            uint owned = OwnedGrade(uid, gunId);
            (uint Grade, uint PresetId) chosen = grades.Any(entry => entry.Grade == owned)
                ? grades.First(entry => entry.Grade == owned)
                : owned > grades[^1].Grade ? grades[^1] : grades.First(entry => entry.Grade > owned);
            GunPresetData? preset = Rows<GunPresetData>().FirstOrDefault(row => row.Id == chosen.PresetId);
            return new TeamGun(gunId, chosen.Grade, chosen.PresetId, preset is null ? 1 : TableFields.U(preset, 6));
        }

        private static StageEnemy ToStageGun(TeamGun gun, uint costume = 0) => new() { GunId = gun.GunId, MNNKMKJFABP = gun.Grade, CostumeId = costume };

        // ---------------------------------------------------------------- info

        private static readonly Dictionary<uint, uint[]> Captured = new()
        {
            [600002] = [55, 67, 68, 74, 82, 83, 90, 51, 61, 76, 52, 58, 72, 78, 84, 85, 87, 53, 57, 59, 65, 50, 60, 63, 75, 77, 79, 81, 49, 69, 70, 71, 73, 80, 89, 54, 62, 64, 66, 86, 88, 56],
            [600003] = [93, 101, 102, 116, 120, 132, 91, 95, 111, 113, 112, 125, 96, 97, 108, 109, 121, 123, 128, 104, 107, 115, 117, 129, 98, 99, 118, 122, 126, 131, 92, 106, 114, 119, 94, 127, 130, 124, 100, 103, 105, 110],
        };

        public HNAJJFJEJGO BuildInfo(uint uid, uint plan)
        {
            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                uint season = SeasonOf(plan);
                var info = new HNAJJFJEJGO
                {
                    PlanId = plan,
                    SeasonId = season,
                    Level = LevelOf(season, state.Exp),
                    Exp = ExpInLevel(season, state.Exp),
                    // 9: difficulties the player already beat (unlocks the next one); 22: the three trends of the season
                    LLCCGNFALPF = { state.Cleared },
                    MMIKNJIBMBA = { state.Talents },
                    EJCFAFMIDBH = { state.GradeTaken },
                    IDGOOPMEEKD = { state.Collection },
                    LDMBJMCDANK = { Captured.GetValueOrDefault(plan, []) },
                    IFNEDNHKBJN = state.Difficulty,
                    ODMEICDCEDP = state.Chip,
                    HIGHJLJPCPB = state.Trend,
                    FEALMJMPBHK = { Trends(season) },
                    PKOFBGMKLLC = { state.ChipOptions },
                };
                foreach (uint gunId in state.Guns)
                    if (PresetOf(uid, gunId) is { } gun)
                        info.NDKGOAHEAMJ.Add(ToStageGun(gun));
                return info;
            }
        }

        /// <summary>RougeGradeData: level (col 2), season (col 3) and experience to reach it (col 4).</summary>
        private List<(uint Level, uint Exp)> GradeLevels(uint season) =>
            Rows<RougeGradeData>().Where(row => row.SeasonId == season).Select(row => (row.Level, row.Exp)).OrderBy(entry => entry.Level).ToList();

        private uint LevelOf(uint season, uint exp)
        {
            uint level = 0;
            uint spent = 0;
            foreach ((uint rowLevel, uint need) in GradeLevels(season))
            {
                if (spent + need > exp)
                    break;
                spent += need;
                level = rowLevel;
            }

            return level;
        }

        private uint ExpInLevel(uint season, uint exp)
        {
            uint spent = 0;
            foreach ((_, uint need) in GradeLevels(season))
            {
                if (spent + need > exp)
                    break;
                spent += need;
            }

            return exp - spent;
        }

        // ---------------------------------------------------------------- talents

        /// <summary>
        /// Sum of the system effect <paramref name="type"/> (<c>RougeTalentData</c> col 5 = {type: value}) of the active talents. Types seen in the tables
        /// and in the client's <c>AEEPMDGFEAD</c>: 1 shop refreshes, 4 starting coins, 5 score-to-experience coefficient (%), 6 skip normal battles of a layer,
        /// 8 unlocks the sudden events, 9 character-choice refreshes, 10 chip-choice refreshes, 11 discount of limited reinforcement. Col 4 = {stat: %} is applied by the client.
        /// </summary>
        private uint TalentEffect(uint season, IEnumerable<uint> active, uint type)
        {
            HashSet<uint> set = active.ToHashSet();
            return (uint)Rows<RougeTalentData>().Where(row => set.Contains(row.Id) && row.SeasonId == season)
                .SelectMany(row => TableFields.Map(row, 5)).Where(pair => pair.Key == type).Sum(pair => (long)pair.Value);
        }

        public HNINDAOJCKP? UnlockTalent(uint uid, uint plan, uint talentId, List<Google.Protobuf.IMessage> messages, out List<uint> unlocked)
        {
            unlocked = [];
            if (!IsPlan(plan))
                return HNINDAOJCKP.RougeUnlockTalentSeason;

            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                unlocked = state.Talents;
                uint season = SeasonOf(plan);
                RougeTalentData? talent = Rows<RougeTalentData>().FirstOrDefault(row => row.Id == talentId && row.SeasonId == season);
                if (talent is null)
                    return HNINDAOJCKP.RougeUnlockTalentSeasonCfg;
                if (state.Talents.Contains(talent.Id))
                    return HNINDAOJCKP.RougeUnlockTalentUnlocked;
                // every listed prerequisite (col 9, 0 = none) has to be active, like the client's tree (PDHLAOGBMGH.JEBMOOJNMLG)
                if (TableFields.List(talent, 9).Any(pre => pre != 0 && !state.Talents.Contains(pre)))
                    return HNINDAOJCKP.RougeUnlockTalentLocked;

                var cost = TableFields.Map(talent, 7).Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => (int)pair.Value);
                var spent = new List<Google.Protobuf.IMessage>();
                if (cost.Count > 0 && !itemUse.Spend(uid, cost, spent))
                    return HNINDAOJCKP.ItemNotEnough;

                messages.AddRange(spent);
                state.Talents.Add(talent.Id);
                Save(uid, plan, state);
                unlocked = state.Talents;
                Log.Information("Rouge: account {Uid} activated talent {Talent} of plan {Plan}.", uid, talent.Id, plan);
                return null;
            }
        }

        // ---------------------------------------------------------------- preparation

        public HNINDAOJCKP? Prepare(uint uid, CS_RougePrepare request)
        {
            uint plan = request.PlanId;
            if (!IsPlan(plan))
                return HNINDAOJCKP.RougePrepareNotInSeason;

            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                if (state.Map is not null)
                    return null;

                uint season = SeasonOf(plan);
                if (request.IFNEDNHKBJN != 0)
                {
                    List<RougeDifficultyData> levels = Difficulties(season);
                    int index = levels.FindIndex(row => TableFields.U(row, 3) == request.IFNEDNHKBJN);
                    if (index < 0)
                        return HNINDAOJCKP.RougePrepareDifficultyInvalid;
                    // RougeDifficultyData col 9 = the level that must be beaten first (0 = open)
                    uint required = TableFields.U(levels[index], 9);
                    if (required != 0 && !state.Cleared.Contains(required) && !state.Cleared.Contains(request.IFNEDNHKBJN))
                        return HNINDAOJCKP.RougePrepareDifficultyLocked;
                    state.Difficulty = request.IFNEDNHKBJN;
                }
                else if (state.Difficulty == 0)
                {
                    state.Difficulty = Difficulties(season).Select(row => TableFields.U(row, 3)).DefaultIfEmpty(1u).Min();
                }

                if (request.HIGHJLJPCPB != 0 && request.HIGHJLJPCPB != state.Trend)
                {
                    if (!Trends(season).Contains(request.HIGHJLJPCPB))
                        return HNINDAOJCKP.RougePrepareSelectChipId;
                    state.Trend = request.HIGHJLJPCPB;
                    state.Chip = 0;
                    state.Guns = [];
                    state.ChipOptions = ChipOptionsOf(state.Trend, state.Difficulty);
                }

                if (request.ODMEICDCEDP != 0 && request.ODMEICDCEDP != state.Chip)
                {
                    if (!state.ChipOptions.Contains(request.ODMEICDCEDP))
                        return HNINDAOJCKP.RougePrepareSelectChipId;
                    state.Chip = request.ODMEICDCEDP;
                }

                if (request.NDKGOAHEAMJ.Count > 0)
                {
                    List<uint> team = request.NDKGOAHEAMJ.Where(id => id != 0).Distinct().ToList();
                    if (team.Count > TeamSize)
                        return HNINDAOJCKP.RougePrepareNoGunPreset;
                    if (team.Any(id => PresetOf(uid, id) is null))
                        return HNINDAOJCKP.RougePrepareNoGunCfg;
                    state.Guns = team;
                }

                Save(uid, plan, state);
                return null;
            }
        }

        /// <summary>
        /// The three opening chips of a trend. ASSUMPTION A-117: the trend names a chip build (<c>RougeOpeningSelectionpoolData</c> col 4 of its pool, at
        /// the chosen level) and the options are three random chips of that build (<c>RougeChipitemByTypeData</c>); a build without chips (season 3 names builds
        /// 15 and 16, which have none) falls back to the chips of the highest existing build.
        /// </summary>
        private List<uint> ChipOptionsOf(uint trend, uint difficulty)
        {
            RougeOpeningSelectionData? selection = Rows<RougeOpeningSelectionData>().FirstOrDefault(row => row.Id == trend);
            if (selection is null)
                return [];

            uint poolId = TableFields.U(selection, 3);
            RougeOpeningSelectionpoolBySelectionPoolData? link = Rows<RougeOpeningSelectionpoolBySelectionPoolData>().FirstOrDefault(row => TableFields.U(row, 1) == poolId);
            Dictionary<uint, RougeOpeningSelectionpoolData> pool = Rows<RougeOpeningSelectionpoolData>().ToDictionary(row => row.Id);
            uint build = (link?.Id ?? [])
                .Where(pool.ContainsKey)
                .Select(id => pool[id])
                .Where(row => TableFields.U(row, 3) == Math.Max(1, difficulty))
                .Select(row => TableFields.U(row, 4))
                .FirstOrDefault();
            Dictionary<uint, IReadOnlyList<uint>> byType = Rows<RougeChipitemByTypeData>().ToDictionary(row => TableFields.U(row, 1), row => (IReadOnlyList<uint>)row.Id.ToList());
            if (!byType.TryGetValue(build, out IReadOnlyList<uint>? chips) || chips.Count == 0)
                chips = byType.OrderByDescending(entry => entry.Key).Select(entry => entry.Value).FirstOrDefault() ?? [];

            return chips.OrderBy(_ => Random.Shared.Next()).Take(OptionCount).ToList();
        }

        public void CancelPrepare(uint uid, uint plan)
        {
            if (!IsPlan(plan))
                return;

            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                if (state.Map is not null)
                    return;

                state.Trend = 0;
                state.Chip = 0;
                state.ChipOptions = [];
                state.Guns = [];
                Save(uid, plan, state);
            }
        }

        // ---------------------------------------------------------------- map

        public PLPBPIBDGNB? GetMap(uint uid, uint plan)
        {
            if (!IsPlan(plan))
                return null;

            lock (AccountLock.For(uid))
                return MapOf(uid, plan, Load(uid, plan));
        }

        /// <summary>
        /// The saved map of a run, brought up to date: a run saved before the multi-layer build (one layer, the last node already finished, no way on) gets
        /// the missing layers of its map table and moves to the next layer when the current one is done (A-126). Saved when it changed.
        /// </summary>
        private PLPBPIBDGNB? MapOf(uint uid, uint plan, RougeState state)
        {
            PLPBPIBDGNB? map = Decode(state.Map);
            if (map is null || Environment.GetEnvironmentVariable("GRIFFIN_ROUGE_LEGACY_MAP") == "1")
                return map;

            bool changed = false;
            uint season = SeasonOf(plan);
            RougeConfigData? config = Rows<RougeConfigData>().FirstOrDefault(row => row.Id == season);
            RougeMapData? mapRow = config is null ? null : Rows<RougeMapData>().FirstOrDefault(row => row.Id == TableFields.U(config, 12));
            if (mapRow is not null)
            {
                int columns = Enumerable.Range(2, 5).Count(c => TableFields.Map(mapRow, c).Count > 0);
                HashSet<uint> used = map.GILKCKNOBGM.Select(l => l.KCGAILCKCLC).ToHashSet();
                for (int column = 2 + map.GILKCKNOBGM.Count; column <= 6 && map.GILKCKNOBGM.Count < columns; column++)
                {
                    uint template = Pick(TableFields.Map(mapRow, column).Where(pair => !used.Contains(pair.Key)).Select(pair => (pair.Key, pair.Value)).ToList());
                    if (template == 0 || BuildLayer(template) is not { } layer)
                        continue;
                    used.Add(template);
                    map.GILKCKNOBGM.Add(layer);
                    changed = true;
                }
            }

            while (map.BCCLAJIJJOA < map.GILKCKNOBGM.Count && map.BCCLAJIJJOA >= 1
                && map.GILKCKNOBGM[(int)map.BCCLAJIJJOA - 1].NNHPACALJEI.Values.Any(n => n.JONLCAGBGJE && NextOf(n.KCIMLBLCCHP).Count == 0))
            {
                map.BCCLAJIJJOA++;
                map.NOIMLDFHELK = 0;
                changed = true;
            }

            if (changed)
            {
                state.Map = Encode(map);
                Save(uid, plan, state);
                Log.Information("Rouge: the saved run of account {Uid} (plan {Plan}) was brought up to date: {Layers} layers, now on layer {Layer}.", uid, plan, map.GILKCKNOBGM.Count, map.BCCLAJIJJOA);
            }

            return map;
        }

        private static PLPBPIBDGNB? Decode(string? text) =>
            string.IsNullOrEmpty(text) ? null : PLPBPIBDGNB.Parser.ParseFrom(Convert.FromBase64String(text));

        private static string Encode(PLPBPIBDGNB map) => Convert.ToBase64String(map.ToByteArray());

        public HNINDAOJCKP? Start(uint uid, uint plan, out PLPBPIBDGNB? map, List<Google.Protobuf.IMessage> messages)
        {
            map = null;
            if (!IsPlan(plan))
                return HNINDAOJCKP.RougeCreateMapNotInSeason;

            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                if (state.Map is not null)
                    return HNINDAOJCKP.RougeCreateMapCreated;
                if (state.Difficulty == 0)
                    return HNINDAOJCKP.RougeCreateMapNoDiff;
                if (state.Chip == 0)
                    return HNINDAOJCKP.RougeCreateMapNoChipId;

                var team = state.Guns.Select(id => PresetOf(uid, id)).Where(gun => gun is not null).Select(gun => gun!).ToList();
                if (team.Count < TeamSize)
                    return HNINDAOJCKP.RougeCreateMapNoGun;

                uint season = SeasonOf(plan);
                RougeConfigData? config = Rows<RougeConfigData>().FirstOrDefault(row => row.Id == season);
                RougeMapData? mapRow = config is null ? null : Rows<RougeMapData>().FirstOrDefault(row => row.Id == TableFields.U(config, 12));
                if (mapRow is null)
                    return HNINDAOJCKP.RougeCreateMapSeasonNoCfg;

                // ASSUMPTION A-118: one layer per column of RougeMapData (cols 2-6 = weighted layer templates, never the same template twice); a template is a
                // graph of RougeMapLayerData rows (col 2 = template, 3 = depth, 4 = RougeMapNodeData, 5 = next nodes). The run ends when the last layer's last node is done.
                var created = new PLPBPIBDGNB { BCCLAJIJJOA = 1, NOIMLDFHELK = 0, IFNEDNHKBJN = state.Difficulty };
                var usedTemplates = new HashSet<uint>();
                for (int column = 2; column <= (Environment.GetEnvironmentVariable("GRIFFIN_ROUGE_LEGACY_MAP") == "1" ? 2 : 6); column++)
                {
                    var options = TableFields.Map(mapRow, column).Where(pair => !usedTemplates.Contains(pair.Key)).Select(pair => (pair.Key, pair.Value)).ToList();
                    uint template = Pick(options);
                    if (template == 0)
                        continue;
                    usedTemplates.Add(template);
                    HBJDPGDHEOI? layer = BuildLayer(template);
                    if (layer is null)
                        return HNINDAOJCKP.RougeCreateMapNoNodes;
                    created.GILKCKNOBGM.Add(layer);
                }

                if (created.GILKCKNOBGM.Count == 0)
                    return HNINDAOJCKP.RougeCreateMapNoLayer;

                created.AMMLMKFDGDK.Add(state.Chip);
                foreach (TeamGun gun in team)
                    created.NDKGOAHEAMJ.Add(ToStageGun(gun));
                state.Map = Encode(created);
                state.GunOptions = [];
                state.GunRefreshUsed = 0;
                state.ChipRefreshUsed = 0;
                // the shop currency starts each run at a fixed amount plus the starting-coin talents (A-124)
                SetCoins(uid, CoinItem(season), (int)(StartCoins(config!) + TalentEffect(season, state.Talents, 4)), messages);
                SetCoins(uid, PointItem(season), 0, messages);
                state.Started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!state.Collection.Contains(state.Chip))
                    state.Collection.Add(state.Chip);
                Save(uid, plan, state);
                map = created;
                Log.Information("Rouge: account {Uid} started plan {Plan} with {Layers} layers.", uid, plan, created.GILKCKNOBGM.Count);
                return null;
            }
        }

        private HBJDPGDHEOI? BuildLayer(uint template)
        {
            var layer = new HBJDPGDHEOI { KCGAILCKCLC = template };
            Dictionary<uint, RougeMapNodeData> nodeData = Rows<RougeMapNodeData>().ToDictionary(row => row.Id);
            foreach (RougeMapLayerData row in Rows<RougeMapLayerData>().Where(row => TableFields.U(row, 2) == template).OrderBy(row => row.Id))
            {
                uint dataId = TableFields.U(row, 4);
                if (!nodeData.TryGetValue(dataId, out RougeMapNodeData? data))
                    continue;

                uint type = TableFields.U(data, 2);
                layer.NNHPACALJEI[row.Id] = new ODFMONHCBIO
                {
                    KCIMLBLCCHP = row.Id,
                    JBEONCNOEEA = dataId,
                    AIODFHGMBME = type,
                    ANJHKELIAFF = ContentOf(type, TableFields.U(data, 3)),
                };
            }

            return layer.NNHPACALJEI.Count == 0 ? null : layer;
        }

        /// <summary>
        /// The content id of a node by type (<c>RougeNodeTableData</c>): 1-4 and 8 are battles (the stage of a weighted <c>RougeBattleData</c> row of the pool),
        /// 5 an event (<c>RougeSelectionPoolData</c> to <c>RougeSelectionDetailData</c>), 6 a shop (<c>RougeShopPoolData</c> to the shop group), 7 a supply
        /// (<c>RougeSupplyPoolData</c>); 9 (gun node) holds the pool itself.
        /// </summary>
        private uint ContentOf(uint type, uint pool)
        {
            switch (type)
            {
                case 1 or 2 or 3 or 4 or 8:
                    return Pick(Rows<RougeBattleData>().Where(row => TableFields.U(row, 2) == pool).Select(row => (TableFields.U(row, 3), TableFields.U(row, 4))).ToList());
                case 5:
                    return Pick(Rows<RougeSelectionPoolData>().Where(row => TableFields.U(row, 2) == pool).Select(row => (TableFields.U(row, 3), TableFields.U(row, 4))).ToList());
                case 6:
                    return Pick(Rows<RougeShopPoolData>().Where(row => TableFields.U(row, 2) == pool).Select(row => (TableFields.U(row, 3), TableFields.U(row, 4))).ToList());
                case 7:
                    return Pick(Rows<RougeSupplyPoolData>().Where(row => TableFields.U(row, 2) == pool).Select(row => (TableFields.U(row, 3), TableFields.U(row, 4))).ToList());
                case 9:
                    return pool;
                default:
                    return pool;
            }
        }

        /// <summary>The node of the layer the run is on (node ids are unique per layer template).</summary>
        private static ODFMONHCBIO? NodeOf(PLPBPIBDGNB map, uint nodeId)
        {
            int index = (int)map.BCCLAJIJJOA - 1;
            if (index < 0 || index >= map.GILKCKNOBGM.Count)
                return null;
            return map.GILKCKNOBGM[index].NNHPACALJEI.TryGetValue(nodeId, out ODFMONHCBIO? node) ? node : null;
        }

        /// <summary>
        /// Marks a node finished. The last node of a layer (no successor in RougeMapLayerData) that is not on the final layer moves the run to the next
        /// layer (current node 0) and the client is sent the new map. ASSUMPTION A-125 (nothing in the tables or the client says who advances the layer).
        /// </summary>
        private void FinishNode(uint plan, PLPBPIBDGNB map, ODFMONHCBIO node, List<Google.Protobuf.IMessage> messages)
        {
            node.JONLCAGBGJE = true;
            if (NextOf(node.KCIMLBLCCHP).Count == 0 && map.BCCLAJIJJOA < map.GILKCKNOBGM.Count)
            {
                map.BCCLAJIJJOA++;
                map.NOIMLDFHELK = 0;
                messages.Add(new SC_GetRougeMapInfo { Map = map, PlanId = plan });
            }
        }

        private IReadOnlyList<uint> NextOf(uint nodeId)
        {
            RougeMapLayerData? row = Rows<RougeMapLayerData>().FirstOrDefault(r => r.Id == nodeId);
            return row is null ? [] : TableFields.List(row, 5);
        }

        private uint DepthOf(uint nodeId)
        {
            RougeMapLayerData? row = Rows<RougeMapLayerData>().FirstOrDefault(r => r.Id == nodeId);
            return row is null ? 0 : TableFields.U(row, 3);
        }

        public HNINDAOJCKP? PreviewNode(uint uid, uint plan, uint nodeId, out ODFMONHCBIO? node)
        {
            node = null;
            lock (AccountLock.For(uid))
            {
                PLPBPIBDGNB? map = MapOf(uid, plan, Load(uid, plan));
                if (map is null)
                    return HNINDAOJCKP.RougeNodePreviewIndexNoData;

                node = NodeOf(map, nodeId);
                return node is null ? HNINDAOJCKP.RougeNodePreviewNodeIdNoCfg : null;
            }
        }

        public HNINDAOJCKP? OpenNode(uint uid, uint plan, uint nodeId, out ODFMONHCBIO? node, out PJDHPAHKGJL? gunPack)
        {
            node = null;
            gunPack = null;
            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                PLPBPIBDGNB? map = MapOf(uid, plan, state);
                if (map is null)
                    return HNINDAOJCKP.RougeNodeOpenIdx;

                node = NodeOf(map, nodeId);
                if (node is null)
                    return HNINDAOJCKP.RougeNodeOpenIdCfg;

                if (map.NOIMLDFHELK != nodeId)
                {
                    if (map.NOIMLDFHELK == 0)
                    {
                        if (DepthOf(nodeId) != 1)
                            return HNINDAOJCKP.RougeNodeOpenNotSecton1;
                    }
                    else
                    {
                        ODFMONHCBIO? current = NodeOf(map, map.NOIMLDFHELK);
                        if (current is null || !current.JONLCAGBGJE)
                            return HNINDAOJCKP.RougeNodeOpenCurrentIdxNotFinished;
                        if (!NextOf(map.NOIMLDFHELK).Contains(nodeId))
                            return HNINDAOJCKP.RougeNodeOpenNotNextIdx;
                    }

                    map.NOIMLDFHELK = nodeId;
                    state.GunOptions = [];
                }

                // the gun node offers its characters as a "pack" (map field 11) that the client shows as a dialog
                if (node.AIODFHGMBME == 9 && !node.JONLCAGBGJE)
                {
                    if (state.GunOptions.Count == 0)
                        state.GunOptions = GunOptionsOf(uid, map, node);
                    gunPack = GunPackOf(uid, state.GunOptions, state.GunRefreshUsed);
                    map.HIOLNHOOLOE = gunPack;
                }

                if (node.AIODFHGMBME == 6 && !node.JONLCAGBGJE && node.Content?.Shop is null)
                    node.Content = new KGDBIAMOCCO { Shop = NewShop(node.ANJHKELIAFF, 0) };

                state.Map = Encode(map);
                Save(uid, plan, state);
                return null;
            }
        }

        // ---------------------------------------------------------------- merchant shop

        private const int ShopSlots = 6;

        private uint CoinItem(uint season) => Rows<RougeConfigData>().Where(row => row.Id == season).Select(row => TableFields.U(row, 15)).FirstOrDefault();

        /// <summary>The talent points (特别情报, col 16 = 9990101/9990111) that a settlement pays.</summary>
        private uint TalentItem(uint season) => Rows<RougeConfigData>().Where(row => row.Id == season).Select(row => TableFields.U(row, 16)).FirstOrDefault();

        private uint PointItem(uint season) => Rows<RougeConfigData>().Where(row => row.Id == season).Select(row => TableFields.U(row, 22)).FirstOrDefault();

        /// <summary>ASSUMPTION A-124: the coins of a new run are RougeConfigData col 17 (80); no table says it.</summary>
        private static uint StartCoins(RougeConfigData config) => TableFields.U(config, 17);

        private int Balance(uint uid, uint item) => itemService.GetPlayerItems(uid).FirstOrDefault(i => i.ItemId == item)?.Count ?? 0;

        private void AddCoins(uint uid, uint item, int delta, List<Google.Protobuf.IMessage> messages)
        {
            if (item == 0 || delta == 0)
                return;
            if (delta > 0)
                rewardService.Grant(uid, item, delta, messages);
            else
                itemUse.Spend(uid, new Dictionary<uint, int> { [item] = -delta }, messages);
        }

        private void SetCoins(uint uid, uint item, int target, List<Google.Protobuf.IMessage> messages) => AddCoins(uid, item, target - Balance(uid, item), messages);

        /// <summary>RougeChipPriceData: col 2 = buy price, col 3 = sell price (selling chip 1242 paid 100 in the 150/100 row).</summary>
        private (int Buy, int Sell) PriceOf(uint chip)
        {
            RougeChipPriceData? row = Rows<RougeChipPriceData>().FirstOrDefault(r => r.Id == chip);
            return row is null ? (0, 0) : ((int)TableFields.U(row, 2), (int)TableFields.U(row, 3));
        }

        /// <summary>The goods of a shop (RougeShopData col 2 = shop group, 3 = chip, 5 = weight): up to six different chips; item.2 = 1 while for sale.</summary>
        private IJGJNMGBIJD NewShop(uint group, uint refreshed)
        {
            var shop = new IJGJNMGBIJD { BBLDDJBCMHL = refreshed };
            var candidates = Rows<RougeShopData>().Where(row => TableFields.U(row, 2) == group)
                .Select(row => (Row: row.Id, Chip: TableFields.U(row, 3), Weight: TableFields.U(row, 5))).ToList();
            while (shop.BIOFMBAOLJO.Count < ShopSlots && candidates.Count > 0)
            {
                uint row = Pick(candidates.Select(c => (c.Row, c.Weight)).ToList());
                uint chip = candidates.First(c => c.Row == row).Chip;
                candidates.RemoveAll(c => c.Chip == chip);
                shop.BIOFMBAOLJO.Add(new EGAKDODPHBE { Id = row, LJJEFHEDHLB = 1 });
            }

            return shop;
        }

        /// <summary>
        /// The shop requests of the client (NodeInteract p1/p2): (row, 0) buys, (0, 1) refreshes, (0, 2) leaves, (chip, 3) sells a chip and
        /// (count, 4) sells grade points (reinforcement terminals, RougePointSellData: coins per unit). ASSUMPTION A-124.
        /// </summary>
        private HNINDAOJCKP? ShopOp(uint uid, uint plan, RougeState state, PLPBPIBDGNB map, ODFMONHCBIO node, uint p1, uint p2, List<Google.Protobuf.IMessage> messages)
        {
            uint season = SeasonOf(plan);
            uint coin = CoinItem(season);
            IJGJNMGBIJD? shop = node.Content?.Shop;
            if (p1 == 0 && p2 == 2)
            {
                FinishNode(plan, map, node, messages);
                return null;
            }

            if (shop is null)
                return HNINDAOJCKP.RougeNodeInteractShopNoData;

            if (p1 == 0 && p2 == 1)
            {
                if (shop.BBLDDJBCMHL >= TalentEffect(season, state.Talents, 1))
                    return HNINDAOJCKP.RougeNodeInteractShopRefreshLimit;
                node.Content = new KGDBIAMOCCO { Shop = NewShop(node.ANJHKELIAFF, shop.BBLDDJBCMHL + 1) };
                return null;
            }

            if (p2 == 0)
            {
                EGAKDODPHBE? good = shop.BIOFMBAOLJO.FirstOrDefault(g => g.Id == p1);
                RougeShopData? row = Rows<RougeShopData>().FirstOrDefault(r => r.Id == p1);
                if (good is null || row is null)
                    return HNINDAOJCKP.RougeNodeInteractShopIdNotInShop;
                if (good.LJJEFHEDHLB == 0)
                    return HNINDAOJCKP.RougeNodeInteractShopSoldOut;
                uint chip = TableFields.U(row, 3);
                int price = PriceOf(chip).Buy;
                if (Balance(uid, coin) < price)
                    return HNINDAOJCKP.ItemNotEnough;
                AddCoins(uid, coin, -price, messages);
                good.LJJEFHEDHLB = 0;
                map.AMMLMKFDGDK.Add(chip);
                if (!state.Collection.Contains(chip))
                    state.Collection.Add(chip);
                messages.Add(new SC_RougeMapChipList { AMMLMKFDGDK = { map.AMMLMKFDGDK }, PlanId = plan });
                return null;
            }

            if (p2 == 3)
            {
                if (!map.AMMLMKFDGDK.Contains(p1))
                    return HNINDAOJCKP.RougeNodeInteractShopIdInvalid;
                map.AMMLMKFDGDK.Remove(p1);
                AddCoins(uid, coin, PriceOf(p1).Sell, messages);
                messages.Add(new SC_RougeMapChipList { AMMLMKFDGDK = { map.AMMLMKFDGDK }, PlanId = plan });
                return null;
            }

            if (p2 == 4)
            {
                uint points = PointItem(season);
                uint unit = Rows<RougePointSellData>().Where(r => r.Id == points).Select(r => TableFields.U(r, 2)).FirstOrDefault();
                if (p1 == 0 || p1 > int.MaxValue || Balance(uid, points) < p1)
                    return HNINDAOJCKP.RougeNodeInteractShopIdInvalid;
                itemUse.Spend(uid, new Dictionary<uint, int> { [points] = (int)p1 }, messages);
                AddCoins(uid, coin, (int)(p1 * unit), messages);
                return null;
            }

            return HNINDAOJCKP.RougeNodeInteractShopIdInvalid;
        }

        /// <summary>Three characters of the gun pool of the node (<c>RougeGunPoolData</c> col 2 = pool, 3 = character, 4 = weight) the squad does not have yet.</summary>
        private List<uint> GunOptionsOf(uint uid, PLPBPIBDGNB map, ODFMONHCBIO node)
        {
            uint pool = node.ANJHKELIAFF;
            HashSet<uint> inTeam = map.NDKGOAHEAMJ.Select(g => g.GunId).ToHashSet();
            var candidates = Rows<RougeGunPoolData>()
                .Where(row => TableFields.U(row, 2) == pool)
                .Select(row => (Gun: TableFields.U(row, 3), Weight: TableFields.U(row, 4)))
                .Where(entry => !inTeam.Contains(entry.Gun) && PresetOf(uid, entry.Gun) is not null)
                .GroupBy(entry => entry.Gun).Select(group => group.First())
                .ToList();
            var picked = new List<uint>();
            while (picked.Count < OptionCount && candidates.Count > 0)
            {
                uint gun = Pick(candidates.Select(c => (c.Gun, c.Weight)).ToList());
                picked.Add(gun);
                candidates.RemoveAll(c => c.Gun == gun);
            }

            return picked;
        }

        private PJDHPAHKGJL GunPackOf(uint uid, IEnumerable<uint> options, uint used)
        {
            var pack = new PJDHPAHKGJL();
            // the client shows "refreshes left" as (talent refreshes) minus this used counter (MCNBGCDDEOP.1)
            pack.MCNBGCDDEOP = new ONBFLDJMOPL { IHIDHNBBBBN = used };
            foreach (uint id in options)
                if (PresetOf(uid, id) is { } gun)
                    pack.HFEIPLDDHIN.Add(ToStageGun(gun));
            return pack;
        }

        public HNINDAOJCKP? InteractNode(uint uid, uint plan, uint nodeId, uint param1, uint param2, out ODFMONHCBIO? node, List<Google.Protobuf.IMessage> messages)
        {
            node = null;
            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                PLPBPIBDGNB? map = MapOf(uid, plan, state);
                if (map is null)
                    return HNINDAOJCKP.RougeNodeInteractIndex;

                node = NodeOf(map, nodeId);
                if (node is null)
                    return HNINDAOJCKP.RougeNodeInteractIndex;
                if (map.NOIMLDFHELK != nodeId)
                    return HNINDAOJCKP.RougeNodeInteractNotCurrentIdx;
                if (node.JONLCAGBGJE)
                    return HNINDAOJCKP.RougeNodeInteractFinished;

                uint season = SeasonOf(plan);
                HNINDAOJCKP? error = null;
                switch (node.AIODFHGMBME)
                {
                    case 1 or 2 or 3 or 4 or 8:
                        // "quick finish" of a normal battle (talent effect type 6 = the layer it works on; the client offers it with p1 = p2 = 0)
                        if (param1 != 0 || param2 != 0 || node.AIODFHGMBME != 1)
                            return HNINDAOJCKP.RougeNodeInteractSkipBattleAllow;
                        if (!SkipLayers(season, state.Talents).Contains(map.BCCLAJIJJOA))
                            return HNINDAOJCKP.RougeNodeInteractSkipBattleUnlock;
                        BattleReward(uid, plan, state, map, node, messages);
                        FinishNode(plan, map, node, messages);
                        break;
                    case 5:
                        error = EventOp(uid, plan, state, map, node, param1, messages);
                        break;
                    case 6:
                        error = ShopOp(uid, plan, state, map, node, param1, param2, messages);
                        break;
                    case 7:
                        error = SupplyOp(uid, plan, state, map, node, param1, param2, messages);
                        break;
                    case 9:
                        // leaving the character pack unused (the pick itself is a RougePackOp)
                        if (param1 != 0)
                            return HNINDAOJCKP.RougeNodeInteractGunParam;
                        map.HIOLNHOOLOE = null;
                        state.GunOptions = [];
                        FinishNode(plan, map, node, messages);
                        break;
                    default:
                        FinishNode(plan, map, node, messages);
                        break;
                }

                if (error is not null)
                    return error;

                state.Map = Encode(map);
                Save(uid, plan, state);
                return null;
            }
        }

        /// <summary>Layers where the "quick finish" of normal battles is unlocked: the values of the talent effect type 6 (RougeTalentData col 5).</summary>
        private HashSet<uint> SkipLayers(uint season, IEnumerable<uint> active)
        {
            HashSet<uint> set = active.ToHashSet();
            return Rows<RougeTalentData>().Where(row => set.Contains(row.Id) && row.SeasonId == season)
                .SelectMany(row => TableFields.Map(row, 5)).Where(pair => pair.Key == 6).Select(pair => pair.Value).ToHashSet();
        }

        // ---------------------------------------------------------------- events, supplies, rewards

        /// <summary>
        /// A sudden event (type 5): the player chooses option p1 (1-3). The effect of each option is the pool in columns 7, 10 and 13 of
        /// RougeSelectionDetailData: a pool of RougeGunPoolData adds a random character to the squad, a pool of RougeRandomPoolData (col 2 pool, 3 chip,
        /// 4 weight) a random plugin the squad does not have; other values (1901-1903, unlock of special events) have no effect yet. ASSUMPTION A-125.
        /// </summary>
        private HNINDAOJCKP? EventOp(uint uid, uint plan, RougeState state, PLPBPIBDGNB map, ODFMONHCBIO node, uint option, List<Google.Protobuf.IMessage> messages)
        {
            if (option is < 1 or > 3)
                return HNINDAOJCKP.RougeNodeInteractSelectionParam;
            RougeSelectionDetailData? detail = Rows<RougeSelectionDetailData>().FirstOrDefault(row => row.Id == node.ANJHKELIAFF);
            if (detail is null)
                return HNINDAOJCKP.RougeNodeInteractSelectionNoCfg;

            uint pool = TableFields.U(detail, (int)(4 + option * 3));
            GrantFromPool(uid, plan, state, map, pool, messages);
            FinishNode(plan, map, node, messages);
            return null;
        }

        /// <summary>A pool of the event table: characters if it is a character pool, plugins otherwise.</summary>
        private void GrantFromPool(uint uid, uint plan, RougeState state, PLPBPIBDGNB map, uint pool, List<Google.Protobuf.IMessage> messages)
        {
            if (pool == 0)
                return;

            var guns = Rows<RougeGunPoolData>().Where(row => TableFields.U(row, 2) == pool)
                .Select(row => (Gun: TableFields.U(row, 3), Weight: TableFields.U(row, 4)))
                .Where(entry => map.NDKGOAHEAMJ.All(g => g.GunId != entry.Gun) && PresetOf(uid, entry.Gun) is not null).ToList();
            if (guns.Count > 0)
            {
                uint gun = Pick(guns.Select(entry => (entry.Gun, entry.Weight)).ToList());
                map.NDKGOAHEAMJ.Add(ToStageGun(PresetOf(uid, gun)!));
                return;
            }

            var chips = Rows<RougeRandomPoolData>().Where(row => TableFields.U(row, 2) == pool)
                .Select(row => (Chip: TableFields.U(row, 3), Weight: TableFields.U(row, 4))).ToList();
            var fresh = chips.Where(entry => !map.AMMLMKFDGDK.Contains(entry.Chip)).ToList();
            if (fresh.Count == 0)
                fresh = chips;
            if (fresh.Count > 0)
            {
                uint chip = Pick(fresh.Select(entry => (entry.Chip, entry.Weight)).ToList());
                map.AMMLMKFDGDK.Add(chip);
                if (!state.Collection.Contains(chip))
                    state.Collection.Add(chip);
                messages.Add(new SC_RougeMapChipList { AMMLMKFDGDK = { map.AMMLMKFDGDK }, PlanId = plan });
            }
        }

        /// <summary>
        /// Limited reinforcement (type 7): (character, 0) raises its grade by one, paying the reinforcement terminals (RougePresetData col 5 of the next grade
        /// minus the discount talent type 11); (0, 2) leaves. The client replaces the character with SC_RougeMapGunModify. ASSUMPTION A-125.
        /// </summary>
        private HNINDAOJCKP? SupplyOp(uint uid, uint plan, RougeState state, PLPBPIBDGNB map, ODFMONHCBIO node, uint p1, uint p2, List<Google.Protobuf.IMessage> messages)
        {
            if (p1 == 0)
            {
                FinishNode(plan, map, node, messages);
                return null;
            }

            StageEnemy? member = map.NDKGOAHEAMJ.FirstOrDefault(g => g.GunId == p1);
            if (member is null)
                return HNINDAOJCKP.RougeNodeInteractGunNoData;
            RougePresetByGunIdData? link = Rows<RougePresetByGunIdData>().FirstOrDefault(row => row.GunId == p1);
            Dictionary<uint, RougePresetData> presets = Rows<RougePresetData>().ToDictionary(row => row.Id);
            RougePresetData? next = (link?.Id ?? []).Where(presets.ContainsKey).Select(id => presets[id]).FirstOrDefault(row => TableFields.U(row, 3) == member.MNNKMKJFABP + 1);
            if (next is null)
                return HNINDAOJCKP.RougeNodeInteractGunParam;

            uint season = SeasonOf(plan);
            long cost = Math.Max(0, (long)TableFields.U(next, 5) - TalentEffect(season, state.Talents, 11));
            if (cost > 0 && !itemUse.Spend(uid, new Dictionary<uint, int> { [PointItem(season)] = (int)cost }, messages))
                return HNINDAOJCKP.ItemNotEnough;

            member.MNNKMKJFABP++;
            messages.Add(new SC_RougeMapGunModify { Gun = member, PlanId = plan });
            return null;
        }

        /// <summary>
        /// What winning (or quick-finishing) a battle node gives. ASSUMPTION A-125 (no table): coins and reinforcement terminals by node type
        /// (normal 30/20, dangerous 50/30, omen 80/40, special 50/30, final 0/0) and a pack of three plugins to choose from
        /// (random pool RougeRandomPoolData 1002, or 2002 in season 3, without the plugins the squad has).
        /// </summary>
        private void BattleReward(uint uid, uint plan, RougeState state, PLPBPIBDGNB map, ODFMONHCBIO node, List<Google.Protobuf.IMessage> messages)
        {
            uint season = SeasonOf(plan);
            (int coins, int points) = node.AIODFHGMBME switch { 1 => (30, 20), 2 => (50, 30), 3 => (80, 40), 8 => (50, 30), _ => (0, 0) };
            AddCoins(uid, CoinItem(season), coins, messages);
            AddCoins(uid, PointItem(season), points, messages);
            if (node.AIODFHGMBME == 4)
                return;

            uint pool = season == 2 ? 1002u : 2002u;
            var chips = Rows<RougeRandomPoolData>().Where(row => TableFields.U(row, 2) == pool && !map.AMMLMKFDGDK.Contains(TableFields.U(row, 3)))
                .Select(row => (Chip: TableFields.U(row, 3), Weight: TableFields.U(row, 4))).GroupBy(e => e.Chip).Select(g => g.First()).ToList();
            var picked = new List<uint>();
            while (picked.Count < OptionCount && chips.Count > 0)
            {
                uint chip = Pick(chips.Select(c => (c.Chip, c.Weight)).ToList());
                picked.Add(chip);
                chips.RemoveAll(c => c.Chip == chip);
            }

            if (picked.Count == 0)
                return;

            map.IELOGFIINGN = ChipPackOf(picked, state.ChipRefreshUsed);
            messages.Add(new SC_RougePackOp { IELOGFIINGN = map.IELOGFIINGN, HIOLNHOOLOE = map.HIOLNHOOLOE, PlanId = plan });
        }

        private static NPFNICNPFGA ChipPackOf(IEnumerable<uint> options, uint used) => new()
        {
            PKOFBGMKLLC = { options },
            MCNBGCDDEOP = new ONBFLDJMOPL { IHIDHNBBBBN = used },
        };

        public HNINDAOJCKP? PackOp(uint uid, uint plan, CS_RougePackOp.Types.AAOPFMBDJHO op, CS_RougePackOp.Types.JAJGPMKINOD target, uint id, List<Google.Protobuf.IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                PLPBPIBDGNB? map = MapOf(uid, plan, state);
                if (map is null)
                    return HNINDAOJCKP.RougeChooseChipNoMap;

                uint season = SeasonOf(plan);
                if (target == CS_RougePackOp.Types.JAJGPMKINOD.OpTargetChip)
                {
                    NPFNICNPFGA? pack = map.IELOGFIINGN;
                    if (pack is null || pack.PKOFBGMKLLC.Count == 0)
                        return HNINDAOJCKP.RougeChooseChipNoPack;

                    switch (op)
                    {
                        case CS_RougePackOp.Types.AAOPFMBDJHO.OpTypePick:
                            if (!pack.PKOFBGMKLLC.Contains(id))
                                return HNINDAOJCKP.RougeChooseChipNotInPack;
                            map.AMMLMKFDGDK.Add(id);
                            if (!state.Collection.Contains(id))
                                state.Collection.Add(id);
                            map.IELOGFIINGN = null;
                            messages.Add(new SC_RougePackOp { HIOLNHOOLOE = map.HIOLNHOOLOE, PlanId = plan });
                            messages.Add(new SC_RougeMapChipList { AMMLMKFDGDK = { map.AMMLMKFDGDK }, PlanId = plan });
                            break;
                        case CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeGiveup:
                            map.IELOGFIINGN = null;
                            messages.Add(new SC_RougePackOp { HIOLNHOOLOE = map.HIOLNHOOLOE, PlanId = plan });
                            break;
                        case CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeRefresh:
                        {
                            if (state.ChipRefreshUsed >= TalentEffect(season, state.Talents, 10))
                                return HNINDAOJCKP.RougeNodeInteractShopRefreshLimit;
                            state.ChipRefreshUsed++;
                            uint pool = season == 2 ? 1002u : 2002u;
                            var again = Rows<RougeRandomPoolData>().Where(row => TableFields.U(row, 2) == pool && !map.AMMLMKFDGDK.Contains(TableFields.U(row, 3)))
                                .Select(row => TableFields.U(row, 3)).Distinct().OrderBy(_ => Random.Shared.Next()).Take(OptionCount).ToList();
                            map.IELOGFIINGN = ChipPackOf(again.Count > 0 ? again : pack.PKOFBGMKLLC, state.ChipRefreshUsed);
                            messages.Add(new SC_RougePackOp { IELOGFIINGN = map.IELOGFIINGN, HIOLNHOOLOE = map.HIOLNHOOLOE, PlanId = plan });
                            break;
                        }
                        default:
                            return HNINDAOJCKP.RougeChooseChipNoPack;
                    }

                    state.Map = Encode(map);
                    Save(uid, plan, state);
                    return null;
                }

                if (target != CS_RougePackOp.Types.JAJGPMKINOD.OpTargetGun)
                    return HNINDAOJCKP.RougeChooseChipNoPack;

                ODFMONHCBIO? node = NodeOf(map, map.NOIMLDFHELK);
                if (node is null || node.AIODFHGMBME != 9 || node.JONLCAGBGJE || state.GunOptions.Count == 0)
                    return HNINDAOJCKP.RougeChooseChipNoPack;

                switch (op)
                {
                    case CS_RougePackOp.Types.AAOPFMBDJHO.OpTypePick:
                    {
                        if (!state.GunOptions.Contains(id) || PresetOf(uid, id) is not { } gun)
                            return HNINDAOJCKP.RougeChooseChipNotInPack;
                        map.NDKGOAHEAMJ.Add(ToStageGun(gun));
                        state.GunOptions = [];
                        map.HIOLNHOOLOE = null;
                        var layerMessages = new List<Google.Protobuf.IMessage>();
                        FinishNode(plan, map, node, layerMessages);
                        messages.Add(new SC_RougePackOp { IELOGFIINGN = map.IELOGFIINGN, PlanId = plan });
                        messages.Add(new SC_RougeNodeInteract { GCMADDJNGMI = node, PlanId = plan });
                        messages.AddRange(layerMessages);
                        break;
                    }
                    case CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeGiveup:
                        state.GunOptions = [];
                        map.HIOLNHOOLOE = null;
                        var giveupMessages = new List<Google.Protobuf.IMessage>();
                        FinishNode(plan, map, node, giveupMessages);
                        messages.Add(new SC_RougePackOp { IELOGFIINGN = map.IELOGFIINGN, PlanId = plan });
                        messages.Add(new SC_RougeNodeInteract { GCMADDJNGMI = node, PlanId = plan });
                        messages.AddRange(giveupMessages);
                        break;
                    case CS_RougePackOp.Types.AAOPFMBDJHO.OpTypeRefresh:
                        if (state.GunRefreshUsed >= TalentEffect(season, state.Talents, 9))
                            return HNINDAOJCKP.RougeNodeInteractShopRefreshLimit;
                        state.GunRefreshUsed++;
                        state.GunOptions = GunOptionsOf(uid, map, node);
                        map.HIOLNHOOLOE = GunPackOf(uid, state.GunOptions, state.GunRefreshUsed);
                        messages.Add(new SC_RougePackOp { IELOGFIINGN = map.IELOGFIINGN, HIOLNHOOLOE = map.HIOLNHOOLOE, PlanId = plan });
                        break;
                    default:
                        return HNINDAOJCKP.RougeChooseChipNoPack;
                }

                state.Map = Encode(map);
                Save(uid, plan, state);
                return null;
            }
        }

        // ---------------------------------------------------------------- battle

        public HNINDAOJCKP? BeginBattle(uint uid, uint plan, uint nodeId, uint stageId)
        {
            if (!IsPlan(plan))
                return HNINDAOJCKP.RougeNodeOpenIdx;

            lock (AccountLock.For(uid))
            {
                PLPBPIBDGNB? map = MapOf(uid, plan, Load(uid, plan));
                ODFMONHCBIO? node = map is null ? null : NodeOf(map, nodeId);
                if (map is null || node is null)
                    return HNINDAOJCKP.RougeNodeOpenIdx;
                if (map.NOIMLDFHELK != nodeId)
                    return HNINDAOJCKP.RougeNodeInteractNotCurrentIdx;
                if (node.JONLCAGBGJE)
                    return HNINDAOJCKP.RougeNodeInteractFinished;
                if (node.AIODFHGMBME is not (1 or 2 or 3 or 4 or 8) || node.ANJHKELIAFF != stageId)
                    return HNINDAOJCKP.RougeNodeInteractNodeType;
                return null;
            }
        }

        public IReadOnlyList<TeamGun> Team(uint uid, uint plan)
        {
            lock (AccountLock.For(uid))
            {
                PLPBPIBDGNB? map = MapOf(uid, plan, Load(uid, plan));
                if (map is null)
                    return [];

                return map.NDKGOAHEAMJ
                    .Select(g => PresetOf(uid, g.GunId))
                    .Where(gun => gun is not null).Select(gun => gun!).ToList();
            }
        }

        public ODFMONHCBIO? EndBattle(uint uid, uint plan, uint nodeId, bool won, List<Google.Protobuf.IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                PLPBPIBDGNB? map = MapOf(uid, plan, state);
                ODFMONHCBIO? node = map is null ? null : NodeOf(map, nodeId);
                if (map is null || node is null)
                    return null;

                if (won && !node.JONLCAGBGJE)
                {
                    // the node message first (it carries the finished flag), then the rewards, the pack and, if the layer ended, the new map
                    var done = node.Clone();
                    done.JONLCAGBGJE = true;
                    messages.Add(new SC_RougeNodeInteract { GCMADDJNGMI = done, PlanId = plan });
                    BattleReward(uid, plan, state, map, node, messages);
                    FinishNode(plan, map, node, messages);
                    state.Map = Encode(map);
                    Save(uid, plan, state);
                }

                return node;
            }
        }

        public HNINDAOJCKP? TakeGradeReward(uint uid, uint plan, IReadOnlyCollection<uint> ids, List<Google.Protobuf.IMessage> messages, out List<uint> taken)
        {
            taken = [];
            if (!IsPlan(plan))
                return HNINDAOJCKP.RougeTakeGradeRewardSeason;

            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                uint season = SeasonOf(plan);
                uint level = LevelOf(season, state.Exp);
                var rows = new List<RougeGradeData>();
                foreach (uint id in ids.Distinct())
                {
                    RougeGradeData? row = Rows<RougeGradeData>().FirstOrDefault(r => r.Id == id && r.SeasonId == season);
                    if (row is null)
                        return HNINDAOJCKP.RougeTakeGradeRewardSeason;
                    if (row.Level > level)
                        return HNINDAOJCKP.RougeTakeGradeRewardLevel;
                    if (state.GradeTaken.Contains(id))
                        return HNINDAOJCKP.RougeTakeGradeRewardPicked;
                    rows.Add(row);
                }

                foreach (RougeGradeData row in rows)
                {
                    foreach ((uint item, uint count) in TableFields.Map(row, 5))
                        rewardService.Grant(uid, item, (int)count, messages);
                    state.GradeTaken.Add(row.Id);
                    taken.Add(row.Id);
                }

                Save(uid, plan, state);
                return null;
            }
        }

        // ---------------------------------------------------------------- settlement

        public HNINDAOJCKP? Finish(uint uid, uint plan, CDPCJHMOCPM reason, out SC_RougeFinish? result, List<Google.Protobuf.IMessage> messages)
        {
            result = null;
            if (!IsPlan(plan))
                return HNINDAOJCKP.RougeFinishNoSeason;

            lock (AccountLock.For(uid))
            {
                RougeState state = Load(uid, plan);
                PLPBPIBDGNB? map = MapOf(uid, plan, state);
                if (map is null)
                    return HNINDAOJCKP.RougeFinishNoMap;

                uint season = SeasonOf(plan);
                // ASSUMPTION A-120: the run is a success when its last node (no successor in RougeMapLayerData) is finished; the client reports "manual quit"
                // both for the quit button and for a finished map, and "battle failure" when the squad lost.
                List<ODFMONHCBIO> nodes = map.GILKCKNOBGM.SelectMany(layer => layer.NNHPACALJEI.Values).ToList();
                bool complete = map.BCCLAJIJJOA == map.GILKCKNOBGM.Count
                    && map.GILKCKNOBGM[^1].NNHPACALJEI.Values.Any(node => node.JONLCAGBGJE && NextOf(node.KCIMLBLCCHP).Count == 0);
                CDPCJHMOCPM outcome = reason == CDPCJHMOCPM.RougeFinishOptionBattleFailure ? reason
                    : complete ? CDPCJHMOCPM.RougeFinishReasonSuccessQuit : reason;

                // settlement rows (RougeSettlementData ids): 1 sections explored, 2 events, 3 normal / 4 hard / 5 boss / 6 final battles
                // ASSUMPTION A-120: the score of each row is its count times a fixed value (no table gives it).
                var rows = new Dictionary<uint, (uint Count, uint Score)>
                {
                    [1] = ((uint)map.GILKCKNOBGM.Count(layer => layer.NNHPACALJEI.Values.Any(n => n.JONLCAGBGJE)), 100),
                    [2] = (0, 50),
                    [3] = (0, 50),
                    [4] = (0, 100),
                    [5] = (0, 200),
                    [6] = (0, 300),
                    [7] = (0, 50),
                    [8] = (0, 50),
                };
                foreach (ODFMONHCBIO node in nodes.Where(node => node.JONLCAGBGJE))
                {
                    uint row = node.AIODFHGMBME switch { 1 => 3u, 2 => 4u, 3 => 5u, 4 => 6u, 5 => 2u, 8 => 8u, 7 => 7u, _ => 0u };
                    if (row == 0)
                        continue;
                    (uint count, uint score) = rows[row];
                    rows[row] = (count + 1, score);
                }

                var settlement = new SC_RougeFinish { PlanId = plan, Reason = outcome, NAIHOEJOHDE = state.Started == 0 ? 0 : Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - state.Started) };
                uint total = 0;
                foreach ((uint type, (uint count, uint unit)) in rows)
                {
                    uint score = count * unit;
                    total += score;
                    settlement.PLDHFBFAKDI.Add(new NGIAKMJGMLG { Type = type, Count = count, Score = score });
                }

                settlement.Score = total;
                settlement.Exp = total + total * TalentEffect(season, state.Talents, 5) / 100;
                settlement.Point = total / 10;
                // ASSUMPTION A-127: the settlement's "point" is paid as talent points (特别情报); the tables give no other source for them
                AddCoins(uid, TalentItem(season), (int)settlement.Point, messages);
                state.Exp += settlement.Exp;
                if (outcome == CDPCJHMOCPM.RougeFinishReasonSuccessQuit && map.IFNEDNHKBJN != 0 && !state.Cleared.Contains(map.IFNEDNHKBJN))
                    state.Cleared.Add(map.IFNEDNHKBJN);

                SetCoins(uid, CoinItem(season), 0, messages);
                SetCoins(uid, PointItem(season), 0, messages);
                state.Map = null;
                state.Trend = 0;
                state.Chip = 0;
                state.ChipOptions = [];
                state.Guns = [];
                state.GunOptions = [];
                Save(uid, plan, state);
                result = settlement;
                Log.Information("Rouge: account {Uid} finished plan {Plan} ({Reason}), score {Score}.", uid, plan, outcome, total);
                return null;
            }
        }
    }
}
