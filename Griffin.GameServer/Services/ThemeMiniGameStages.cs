using Google.Protobuf;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Stage rules of the event mini games played through CS_ThemeMiniGame* (client 4.0.5142), all read from the tables of the mini game's
    /// own configuration row (<see cref="ThemeModules.Config"/>, ActivityModuleData column 4), never from the theme id:
    /// - CandyCatch (23002): CandycatchMainData[config] column 2 = stage group; CandycatchStageData column 4 = group, column 2 = stage that must
    ///   be cleared first (the client, PNBFMLIHPJH.IILOHDKDJJN, opens a stage when that record has stars), column 3 = hours after the opening
    ///   (checked by the client), column 6 = star ids (CandycatchStarData: column 3 reward, column 4 score needed).
    /// - CardFlip (23003): CardflipMainData[config] column 2 = group; CardflipStageData column 3 = group, column 2 = previous stage,
    ///   column 7 = star ids (CardflipStarData: column 3 reward, column 4 condition). The client shows a stage open from field 2 of its record.
    /// - Audition (23001): AuditionMainData[config] column 2 = group; AuditionStageData column 4 = group, column 2 = previous stage.
    /// - Motorcycle (24001): MotorcycleBaseData[config] column 4 = mission group (MotorcycleMissionData column 3; column 5 = unlock rows),
    ///   column 6 = skill group (MotorcycleSkillData column 2; column 9 = skill learned first, column 10 = cost, column 11 = unlock row).
    ///   MotorcycleUnlockData: column 4 condition type, column 5 its argument.
    /// </summary>
    public static class ThemeMiniGameStages
    {
        public sealed class StageRecord
        {
            public bool Cleared { get; set; }
            public uint Best { get; set; }
            public uint Stars { get; set; }
            /// <summary>Second record value: card flip LELAOAJLKKI (best reported count), audition NHALLNKPCKG, motorcycle coins.</summary>
            public uint Extra { get; set; }
            public uint Duration { get; set; }
            public uint Rank { get; set; }
        }

        public sealed class State
        {
            public Dictionary<uint, StageRecord> Stages { get; set; } = [];
            public List<uint> ClaimedStars { get; set; } = [];
            public List<uint> Skills { get; set; } = [];
            public uint Points { get; set; }
            public uint Playing { get; set; }
        }

        public static string Key(uint theme, uint module) => $"theme-minigame.stages.v1.{theme}.{module}";

        public static bool IsStageGame(uint module) =>
            module is ThemeModules.CandyCatch or ThemeModules.CardFlip or ThemeModules.Audition or ThemeModules.Motorcycle;

        /// <summary>One stage of the configuration: id, the stage to clear first (0 = none), the unlock rows (motorcycle) and the star ids.</summary>
        public sealed record Stage(uint Id, uint Previous, IReadOnlyList<uint> UnlockRows, IReadOnlyList<uint> Stars);

        public static IReadOnlyList<Stage> Stages(ITableService tables, uint theme, uint module)
        {
            uint config = ThemeModules.Config(tables, theme, module);
            if (config == 0)
                return [];
            switch (module)
            {
                case ThemeModules.CandyCatch:
                {
                    IMessage? main = tables.GetTable<CandycatchMainData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
                    if (main is null) return [];
                    uint group = TableFields.U(main, 2);
                    return tables.GetTable<CandycatchStageData>().Where(r => TableFields.U(r, 4) == group && TableFields.U(r, 1) != 0)
                        .Select(r => new Stage(TableFields.U(r, 1), TableFields.U(r, 2), [], TableFields.List(r, 6))).ToList();
                }
                case ThemeModules.CardFlip:
                {
                    IMessage? main = tables.GetTable<CardflipMainData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
                    if (main is null) return [];
                    uint group = TableFields.U(main, 2);
                    return tables.GetTable<CardflipStageData>().Where(r => TableFields.U(r, 3) == group && TableFields.U(r, 1) != 0)
                        .Select(r => new Stage(TableFields.U(r, 1), TableFields.U(r, 2), [], TableFields.List(r, 7))).ToList();
                }
                case ThemeModules.Audition:
                {
                    IMessage? main = tables.GetTable<AuditionMainData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
                    if (main is null) return [];
                    uint group = TableFields.U(main, 2);
                    // Column 6 holds ids of another star table that this build does not ship (no AuditionStarData), so no star rewards here.
                    return tables.GetTable<AuditionStageData>().Where(r => TableFields.U(r, 4) == group && TableFields.U(r, 1) != 0)
                        .Select(r => new Stage(TableFields.U(r, 1), TableFields.U(r, 2), [], [])).ToList();
                }
                case ThemeModules.Motorcycle:
                {
                    IMessage? main = MotorcycleBase(tables, theme);
                    if (main is null) return [];
                    uint group = TableFields.U(main, 4);
                    return tables.GetTable<MotorcycleMissionData>().Where(r => TableFields.U(r, 3) == group && TableFields.U(r, 1) != 0)
                        .Select(r => new Stage(TableFields.U(r, 1), 0, TableFields.List(r, 5), [])).ToList();
                }
                default:
                    return [];
            }
        }

        public static IMessage? MotorcycleBase(ITableService tables, uint theme)
        {
            uint config = ThemeModules.Config(tables, theme, ThemeModules.Motorcycle);
            return config == 0 ? null : tables.GetTable<MotorcycleBaseData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
        }

        /// <summary>Skills of the motorcycle configuration: id, skill learned first, cost, unlock row.</summary>
        public static IReadOnlyList<(uint Id, uint Previous, uint Cost, uint Unlock)> MotorcycleSkills(ITableService tables, uint theme)
        {
            IMessage? main = MotorcycleBase(tables, theme);
            if (main is null) return [];
            uint group = TableFields.U(main, 6);
            return tables.GetTable<MotorcycleSkillData>().Where(r => TableFields.U(r, 2) == group && TableFields.U(r, 1) != 0)
                .Select(r => (TableFields.U(r, 1), TableFields.U(r, 9), TableFields.U(r, 10), TableFields.U(r, 11))).ToList();
        }

        /// <summary>
        /// Riders (MotorcycleGunData) the player can pick: the client shows a rider unlocked only when its id is in the info's field 3
        /// (client MLBFMHPHJIG.FBMFLJFNMMP over HKDHBDCBDIP); an empty list showed every rider locked. A rider of the configuration (column 2
        /// = MotorcycleBaseData column 4 group, ASSUMPTION: columns 4-6 hold the same group in this build) is unlocked when it needs no
        /// unlock (column 5 = 0) or its MotorcycleUnlockData row (column 4) is met (A-220).
        /// </summary>
        public static IReadOnlyList<uint> MotorcycleRiders(ITableService tables, State state, uint theme)
        {
            IMessage? main = MotorcycleBase(tables, theme);
            if (main is null) return [];
            uint group = TableFields.U(main, 4);
            return tables.GetTable<MotorcycleGunData>()
                .Where(r => TableFields.U(r, 1) != 0 && TableFields.U(r, 2) == group
                    && (TableFields.U(r, 5) == 0 || UnlockMet(tables, state, TableFields.U(r, 4))))
                .Select(r => TableFields.U(r, 1)).ToList();
        }

        /// <summary>
        /// A MotorcycleUnlockData row is met when its condition holds. ASSUMPTION: condition type 1 (column 4) means "stage column 5 cleared";
        /// every row of this build uses type 1 with a MotorcycleMissionData id as argument. Unknown types count as not met.
        /// </summary>
        public static bool UnlockMet(ITableService tables, State state, uint unlockRow)
        {
            if (unlockRow == 0) return true;
            IMessage? row = tables.GetTable<MotorcycleUnlockData>().FirstOrDefault(r => TableFields.U(r, 1) == unlockRow);
            if (row is null) return false;
            return TableFields.U(row, 4) == 1 && state.Stages.TryGetValue(TableFields.U(row, 5), out StageRecord? rec) && rec.Cleared;
        }

        public static bool Unlocked(ITableService tables, State state, Stage stage) =>
            (stage.Previous == 0 || (state.Stages.TryGetValue(stage.Previous, out StageRecord? prev) && prev.Cleared))
            && stage.UnlockRows.All(u => UnlockMet(tables, state, u));

        /// <summary>Star rows (id, reward, condition) of a candy catch or card flip stage.</summary>
        public static IReadOnlyList<(uint Id, IReadOnlyList<(uint Key, uint Value)> Reward, uint Condition)> StarRows(ITableService tables, uint module, Stage stage)
        {
            IEnumerable<IMessage> rows = module switch
            {
                ThemeModules.CandyCatch => tables.GetTable<CandycatchStarData>(),
                ThemeModules.CardFlip => tables.GetTable<CardflipStarData>(),
                _ => [],
            };
            Dictionary<uint, IMessage> byId = rows.GroupBy(r => TableFields.U(r, 1)).ToDictionary(g => g.Key, g => g.First());
            return stage.Stars.Where(byId.ContainsKey).Select(id => (id, TableFields.Map(byId[id], 3), TableFields.U(byId[id], 4))).ToList();
        }
    }
}
