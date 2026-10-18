using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.GameServer.Extensions;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Encodes the units of a <c>StageBattle</c> camp (<c>StageBattle.camps[].units</c>) with the wire layout of client 4.0.5136.
    /// The checked-in generated schema predates that layout (<c>OPADLDPIIKL.LECCHKLIOOK</c> is empty), so the bytes are written by
    /// hand and parsed back into the camp message as protobuf unknown fields.
    ///
    /// Layout (taken from the client's own builders, <c>HJJPBCDDLBA</c>/<c>OKPAHBPPCGO</c>):
    /// <list type="bullet">
    /// <item><description>camp: 1 = side (<b>1 = player, 2 = enemy</b>; 0 matches nothing the client looks for), 2 = repeated unit.</description></item>
    /// <item><description>unit (oneof): 1 = Gun, 2 = Enemy, 3 = Robot, 4 = Device, 5 = Support.</description></item>
    /// <item><description>Gun unit: 1 = the character (<c>Gun</c>), 2 = deployment point id, 10 = <c>Property</c> (empty, legacy),
    /// 3 = rotation text, 11 = skill message, 12 = <c>InheritProperty</c> (empty), 22 = developed properties <c>{1: map&lt;int,int&gt;}</c> (key = column of
    /// the <c>PropertyData</c> row = <c>DevelopProperty</c> value), 23 = a second empty property map the client always creates.</description></item>
    /// <item><description>Skill message (field 11 of the gun unit): 6 = repeated skill id.</description></item>
    /// </list>
    /// Assumptions, because there is no capture of an official <c>SC_StageInto</c>: field 22 (not 23) carries the stat map; skill
    /// field 6 is the "skill list" of the client's builder (its first repeated id field); the stats are the base row of the gun
    /// without level, class, weapon or module scaling (the tutorial units are level 1).
    /// </summary>
    /// <summary>What the account adds to the stats of a character besides its own tables (A-73): the character, the control room level and the stages passed.</summary>
    public sealed record StatInputs(GunEntity Gun, uint ControlRoomLevel, IReadOnlySet<uint> PassedStages, IReadOnlyList<(int Property, int Value)> ModAffixes, IReadOnlyList<(uint Stc, ulong Affixes)> PublicKeys, IReadOnlyList<uint> PluginAffixes, IReadOnlyList<uint> FoodRows, IReadOnlyList<uint> SupportRows, IReadOnlyList<(uint Skill, int[] Values)>? ModSkills = null);

    public static class BattleUnitBuilder
    {
        /// <summary>The side numbers of <c>StageBattle.side</c>, <c>user_sides</c> and the camps: the player is 1, the opposing camp 2.</summary>
        public const FMMHAOHCKFN PlayerSide = FMMHAOHCKFN.Player1;

        public const FMMHAOHCKFN EnemySide = FMMHAOHCKFN.Player2;

        /// <summary>Facing of a deployed unit as the client writes it (<c>StageConfigData</c> lists it per point, e.g. <c>(0.0, 180.0, 0.0)</c>).</summary>
        public const string DefaultRotation = "(0.0, 0.0, 0.0)";

        /// <summary>Gun columns of <c>GunData</c>: 25 = candidate <c>PropertyData</c> rows (the first existing one is the base stats).</summary>
        private const int GunPropertyRowsColumn = 25;

        private const int GunPropertyColumn = 40;

        /// <summary><c>GunGradeData</c> column 11 = the battle skills of the character at that grade (comma separated).</summary>
        private const int GradeSkillSetColumn = 11;

        /// <summary><c>GunGradeData</c> column 5 (<c>add_abbr</c>) = the extra passives/skills of that grade (ids of <c>BattleSkillData</c>).</summary>
        private const int GradeExtraSkillsColumn = 5;

        /// <summary><c>GunData</c> columns: 17 = default skin (4017 for gun 1017), 25 (first value) = the character's own weapon (stc id, same list as the property rows).</summary>
        private const int GunDefaultSkinColumn = 17;

        private const int GunWeaponColumn = GunPropertyRowsColumn;

        /// <summary>Highest <c>PropertyData</c> column read as a stat (the columns above describe other things).</summary>
        private const int LastStatColumn = 82;

        /// <summary>Attack (<c>pow</c>), column 2 of <c>PropertyData</c>.</summary>
        private const int AttackColumn = 2;

        /// <summary>
        /// <c>PropertyData</c> column → <c>DevelopProperty</c> value, matched by NAME from the client's own metadata (<c>StaticTable.dll</c>:
        /// the table's field numbers 2 = Pow, 3 = MaxHp, 4 = MaxShieldHp, 5 = ShieldArmor, 6 = ShieldDamage, 7 = MaxAp, 8 = MaxWillValue,
        /// 9 = Crit, 10 = CritMult... against the enum <c>Pow = 1, MaxHp = 2, MaxShieldHp = 3, ShieldArmor = 4, ShieldDamage = 5, MaxAp = 6,
        /// SuppressValue = 7, MaxWillValue = 8, Crit = 9, CritMult = 10...</c>). The two numberings differ: columns 2-7 are shifted by one,
        /// the enum has <c>SuppressValue</c> (7) that the table lacks, and from column 51 the order changes (51 MaxPotential = 56, 52
        /// RoundPotential = 53...). Sending columns as keys made attack land in MaxHp (and HP in MaxShieldHp), so every hit did 0 damage.
        /// </summary>
        private static readonly Dictionary<int, int> ColumnToDevelopProperty = new()
        {
            [2] = 1, [3] = 2, [4] = 3, [5] = 4, [6] = 5, [7] = 6, [8] = 8, [9] = 9,
            [10] = 10, [11] = 11, [12] = 12, [13] = 13, [14] = 14, [15] = 15, [16] = 16, [17] = 17,
            [18] = 18, [19] = 19, [20] = 20, [21] = 21, [22] = 22, [23] = 23, [24] = 24, [25] = 25,
            [26] = 26, [27] = 27, [28] = 28, [29] = 29, [30] = 30, [31] = 31, [32] = 33, [33] = 34,
            [34] = 35, [35] = 36, [36] = 37, [37] = 38, [38] = 39, [39] = 40, [40] = 41, [41] = 42,
            [42] = 43, [43] = 44, [44] = 45, [45] = 46, [46] = 47, [47] = 48, [48] = 49, [49] = 50,
            [50] = 51, [51] = 56, [52] = 53, [53] = 54, [54] = 55, [55] = 57, [56] = 58, [57] = 59,
            [58] = 60, [59] = 61, [60] = 62, [61] = 67, [62] = 63, [63] = 64, [64] = 65, [65] = 66,
            [66] = 69, [67] = 70, [68] = 71, [69] = 72, [70] = 73, [71] = 74, [72] = 75, [73] = 76,
            [74] = 68, [75] = 77, [76] = 78, [77] = 79, [78] = 80, [79] = 81, [80] = 82, [81] = 83,
            [82] = 84, [83] = 201, [84] = 202, [85] = 203, [86] = 204, [87] = 205, [88] = 206, [89] = 207,
            [90] = 208, [91] = 209, [92] = 210, [93] = 211, [94] = 212, [95] = 213, [96] = 214, [97] = 215,
            [98] = 216, [99] = 217, [100] = 218, [101] = 219, [102] = 220, [103] = 221, [104] = 222, [105] = 223,
            [106] = 224, [107] = 85, [108] = 86, [109] = 225, [110] = 226, [111] = 227,
        };

        /// <summary>A <c>DevelopProperty</c> the client's builder always sets to 1 for characters.</summary>
        private const int AlwaysOneProperty = 77;

        private static Dictionary<uint, Dictionary<int, int>>? statRows;

        private static readonly object StatRowsLock = new();

        /// <summary>The camp message of one side from already encoded units (each one a complete oneof wrapper).</summary>
        public static OPADLDPIIKL Camp(FMMHAOHCKFN side, IEnumerable<byte[]> units)
        {
            byte[] bytes = Encode(output =>
            {
                output.WriteTag(1, WireFormat.WireType.Varint);
                output.WriteEnum((int)side);
                foreach (byte[] unit in units)
                    WriteBytes(output, 2, unit);
            });
            return OPADLDPIIKL.Parser.ParseFrom(bytes);
        }

        /// <summary>
        /// The oneof wrapper (<c>Gun</c> case) of one character deployed at <paramref name="pointId"/>. <paramref name="weapon"/> is the
        /// character's own weapon (null when the account has none: the weapon of the character's table row is used).
        /// </summary>
        public static byte[] GunUnit(ITableService tables, uint pointId, uint gunId, Gun character, WeaponEntity? weapon = null, string rotation = DefaultRotation, StatInputs? inputs = null)
        {
            byte[] gunUnit = Encode(output =>
            {
                WriteBytes(output, 1, WithBattleData(tables, gunId, character, weapon).ToByteArray());
                output.WriteTag(2, WireFormat.WireType.Varint);
                output.WriteUInt32(pointId);
                WriteBytes(output, 3, System.Text.Encoding.UTF8.GetBytes(rotation));
                WriteBytes(output, 10, []);
                WriteBytes(output, 11, SkillMessage(tables, gunId, character.Grade, weapon?.WeaponId ?? DefaultWeaponOf(tables, gunId), (uint)Math.Max(1, weapon?.BreakTimes ?? 1), CoreSkills(tables, inputs), KeySkills(tables, inputs), inputs?.ModSkills, inputs?.Gun.PrivateTalentSkillItems));
                WriteBytes(output, 12, []);
                WriteBytes(output, 22, PropertyMap(StatsOf(tables, gunId, weapon, character.Level, character.GunClass, inputs)));
                WriteBytes(output, 23, []);
            });
            return Encode(output => WriteBytes(output, 1, gunUnit));
        }

        /// <summary>
        /// The client's battle unit factory dereferences parts of the character that the lobby list never needs (the 12:03 client log: a
        /// NullReferenceException in the unit factory right after the camp arrived; the client's own builder refuses a unit without
        /// "skin" or "weapon data"): the skin (<c>GunData</c> column 17 when the character has none) and <c>details_.weapon</c> /
        /// <c>details_.weapon_lite</c> (the character's weapon; a level-1 weapon of <c>GunData</c> column 25 when it has none).
        /// </summary>
        private static Gun WithBattleData(ITableService tables, uint gunId, Gun character, WeaponEntity? weapon)
        {
            Gun unit = character.Clone();
            GunData? row = tables.GetTable<GunData>().FirstOrDefault(r => r.Id == gunId);
            if (unit.Costume == 0 && row is not null)
                unit.Costume = TableFields.U(row, GunDefaultSkinColumn);

            uint weaponStcId = weapon?.WeaponId ?? DefaultWeaponOf(tables, gunId);
            if (weaponStcId == 0)
                return unit;

#pragma warning disable CS0612, CS0618 // GunWeapon is marked deprecated in the generated schema but the client still reads it
            unit.Details = new Gun.Types.Details
            {
                Weapon = new GunWeapon
                {
                    Id = weapon?.Id ?? 0,
                    StcId = weaponStcId,
                    GunId = gunId,
                    Level = (uint)(weapon?.Level ?? 1),
                    Exp = weapon?.CurExp ?? 0,
                    BreakTimes = (uint)(weapon?.BreakTimes ?? 1),
                    Timestamp = unit.Timestamp,
                },
                WeaponLite = weapon?.ToProtoWeapon() ?? new GunWeaponLite { StcId = weaponStcId, Level = 1, BreakTimes = 1 },
            };
#pragma warning restore CS0612, CS0618
            return unit;
        }

        /// <summary>DevelopProperty -> PropertyData column (inverse of <see cref="ColumnToDevelopProperty"/>).</summary>
        private static readonly Dictionary<int, int> DevelopToColumn = ColumnToDevelopProperty.ToDictionary(pair => pair.Value, pair => pair.Key);

        /// <summary>Properties that have a percentage twin (client <c>MPAGLAMJGIK.FBLAIHJNNPI</c>): Pow, MaxHp, MaxShieldHp, ShieldArmor, MaxWillValue, SuppressValue.</summary>
        private static readonly Dictionary<int, int> PercentTwin = new() { [1] = 29, [2] = 30, [3] = 34, [4] = 33, [8] = 31, [7] = 32 };

        /// <summary>The non-zero stats of a PropertyData row keyed by DevelopProperty (empty when the row does not exist).</summary>
        public static IReadOnlyDictionary<int, int> RowByDevelopProperty(ITableService tables, uint rowId)
        {
            var result = new Dictionary<int, int>();
            if (StatRows(tables).TryGetValue(rowId, out Dictionary<int, int>? row))
                foreach ((int column, int value) in row)
                    if (ColumnToDevelopProperty.TryGetValue(column, out int dev) && value > 0)
                        result[dev] = value;
            return result;
        }

        private static int Column(Dictionary<int, int>? row, int developProperty) =>
            row is not null && DevelopToColumn.TryGetValue(developProperty, out int column) ? row.GetValueOrDefault(column) : 0;

        /// <summary>The client's rounding (<c>GFMath.CeilToInt</c> after an integer check with 0.001 of tolerance).</summary>
        private static int CeilLike(double value)
        {
            int whole = (int)value;
            return Math.Abs(value - whole) <= 0.001 ? whole : (int)Math.Ceiling(value);
        }

        /// <summary>
        /// The final stat map of a character, keyed by <c>DevelopProperty</c>, computed like the client does for its own attribute panel
        /// (<c>FJLGHAHLHML.KLNEOKCKAFN</c>, A-71/A-72):
        /// <list type="bullet">
        /// <item><description>base: PropertyData row of the character (<c>GunData</c> column 40) x level row (<c>GunLevelExpData</c> column 4) / 1000, rounded up
        /// (a column the level row does not list becomes 0, as in the client);</description></item>
        /// <item><description>class: every <c>GunClassData</c> row of the character's group (<c>GunData</c> column 35) up to its class (column 3) adds its
        /// PropertyData row (column 8);</description></item>
        /// <item><description>weapon: |ceil(base row of the weapon (<c>GunWeaponData</c> column 21) x level row (<c>GunWeaponExpData</c> id = level x 100 + rank
        /// (column 11), column 9) / 1000) + break row (<c>WeaponBreakData</c> id = weapon x 10 + breaks, column 5)|;</description></item>
        /// <item><description>Pow, MaxHp, MaxShieldHp, ShieldArmor, MaxWillValue and SuppressValue then multiply by (1 + the sum of the same sources of their percentage
        /// twin / 1000); every other property is just the sum.</description></item>
        /// </list>
        /// Not applied (the server keeps no data for them or the table link is unknown): remoulding core, talents, weapon parts, equipment, training slots.
        /// </summary>
        public static IReadOnlyDictionary<int, int> StatsOf(ITableService tables, uint gunId, WeaponEntity? weapon, uint level = 1, uint gunClass = 1, StatInputs? inputs = null)
        {
            GunData? gun = tables.GetTable<GunData>().FirstOrDefault(row => row.Id == gunId);
            Dictionary<uint, Dictionary<int, int>> rows = StatRows(tables);
            Dictionary<int, int>? Row(uint id) => rows.TryGetValue(id, out Dictionary<int, int>? row) ? row : null;

            Dictionary<int, int>? baseRow = Row(gun is null ? 0 : TableFields.U(gun, GunPropertyColumn));
            GunLevelExpData? levelData = tables.GetTable<GunLevelExpData>().FirstOrDefault(row => row.Level == Math.Max(1u, level));
            Dictionary<int, int>? levelRow = levelData is null ? null : Row(TableFields.U(levelData, 4));

            // every class row up to the current class (the rows grow with the class, but the client adds them all)
            uint group = gun is null ? 0 : TableFields.U(gun, 35);
            List<Dictionary<int, int>> classRows = [];
            foreach (GunClassData classData in tables.GetTable<GunClassData>().Where(row => TableFields.U(row, 2) == group && TableFields.U(row, 3) <= Math.Max(1u, gunClass)))
                if (Row(TableFields.U(classData, 8)) is { } classRow)
                    classRows.Add(classRow);

            // rows added flat and as percentage by their columns, like the class rows: affection levels and the keys in the private slots
            List<Dictionary<int, int>> extraRows = [];
            Dictionary<int, int> coreFlat = [];
            Dictionary<int, int> controlRoomFlat = [];
            Dictionary<int, int> keyValues = [];
            List<Dictionary<int, int>> allAddRows = [];
            if (inputs is not null)
            {
                // affection (LoveLevelData id = level, column 8 + the character id x 10 = a PropertyData row), every level up to the current one
                for (uint loveLevel = 1; loveLevel <= inputs.Gun.LoveLevel; loveLevel++)
                {
                    LoveLevelData? love = tables.GetTable<LoveLevelData>().FirstOrDefault(row => TableFields.U(row, 1) == loveLevel);
                    uint loveRow = love is null ? 0 : TableFields.U(love, 8);
                    if (loveRow != 0 && Row(loveRow + gunId * 10) is { } loveStats)
                        extraRows.Add(loveStats);
                }

                // the vow (level 9) adds the row LoveConfigData column 10 (the client's IIEJCPCDKOC), flat and as percentage
                if (inputs.Gun.LoveLevel >= Handlers.LoveHandler.VowLevel
                    && tables.GetTable<LoveConfigData>().FirstOrDefault() is { } loveConfig && Row(TableFields.U(loveConfig, 10)) is { } vowStats)
                    extraRows.Add(vowStats);

                // talent keys in the private slots: TalentKeyData column 5 = a PropertyData row
                foreach (uint key in inputs.Gun.PrivateTalentSkillItems.Where(item => item != 0))
                {
                    TalentKeyData? keyData = tables.GetTable<TalentKeyData>().FirstOrDefault(row => TableFields.U(row, 1) == key);
                    if (keyData is not null && Row(TableFields.U(keyData, 5)) is { } keyStats)
                        extraRows.Add(keyStats);
                }

                // core chips of the support formation used in battle: they apply to every character (A-77)
                foreach (uint supportRow in inputs.SupportRows)
                    if (Row(supportRow) is { } supportStats)
                        extraRows.Add(supportStats);

                // dishes given to the character (A-76): the row of the level each property reached (LobbyCookPropertyData column 6), flat and as percentage
                foreach (uint foodRow in inputs.FoodRows)
                    if (Row(foodRow) is { } foodStats)
                        extraRows.Add(foodStats);

                // Neural Helix (A-76): every unlocked enhancement node (SquadTalentGroupData column 6 = 1) gives the property row of its effect
                // (column 8 lists GroupTalentEffectGeneData rows, one per node level; column 4 of the row = a PropertyData row), and every two evolved nodes
                // (column 6 = 3) raise a bonus: TalentBonusData (group = SquadTalentGunData column 14, threshold = column 3, row = column 4).
                int evolvedNodes = 0;
                foreach ((ulong point, uint nodeLevel) in inputs.Gun.TalentTree)
                {
                    SquadTalentGroupData? node = tables.GetTable<SquadTalentGroupData>().FirstOrDefault(row => TableFields.U64(row, 1) == point);
                    if (node is null)
                        continue;

                    uint nodeType = TableFields.U(node, 6);
                    if (nodeType == 3)
                        evolvedNodes++;
                    if (nodeType != 1)
                        continue;

                    foreach (ulong geneId in TableFields.U64List(node, 8))
                    {
                        GroupTalentEffectGeneData? gene = tables.GetTable<GroupTalentEffectGeneData>().FirstOrDefault(row => TableFields.U64(row, 1) == geneId);
                        if (gene is not null && TableFields.U(gene, 3) == Math.Max(1u, nodeLevel) && Row(TableFields.U(gene, 4)) is { } nodeStats)
                        {
                            extraRows.Add(nodeStats);
                            break;
                        }
                    }
                }

                SquadTalentGunData? tree = tables.GetTable<SquadTalentGunData>().FirstOrDefault(row => TableFields.U(row, 1) == gunId);
                TalentBonusByTalentBonusGroupData? bonusGroup = tree is null ? null
                    : tables.GetTable<TalentBonusByTalentBonusGroupData>().FirstOrDefault(row => TableFields.U(row, 1) == TableFields.U(tree, 14));
                if (bonusGroup is not null)
                {
                    TalentBonusData? bonus = TableFields.List(bonusGroup, 2)
                        .Select(id => tables.GetTable<TalentBonusData>().FirstOrDefault(row => TableFields.U(row, 1) == id))
                        .Where(row => row is not null && TableFields.U(row, 3) <= evolvedNodes)
                        .OrderByDescending(row => TableFields.U(row!, 3)).FirstOrDefault();
                    if (bonus is not null && Row(TableFields.U(bonus, 4)) is { } bonusStats)
                        extraRows.Add(bonusStats);
                }

                // public keys in the public slots (A-75): the base row of the key (TalentKeyData column 5) and its affixes (type 4 keys do not count)
                foreach ((uint stc, ulong affixes) in inputs.PublicKeys)
                {
                    TalentKeyData? keyData = tables.GetTable<TalentKeyData>().FirstOrDefault(row => TableFields.U(row, 1) == stc);
                    if (keyData is null || TableFields.U(keyData, 2) == 4)
                        continue;

                    var keyBase = new Dictionary<int, int>();
                    if (Row(TableFields.U(keyData, 5)) is { } keyRow)
                        foreach ((int column, int value) in keyRow)
                            if (ColumnToDevelopProperty.TryGetValue(column, out int dev) && value > 0)
                                keyBase[dev] = value;

                    foreach ((int dev, int value) in keyBase)
                        keyValues[dev] = keyValues.GetValueOrDefault(dev) + value;
                    foreach ((int dev, int value) in KeyAffixes.AffixValues(tables, affixes, stc, keyBase))
                        keyValues[dev] = keyValues.GetValueOrDefault(dev) + value - keyBase.GetValueOrDefault(dev);
                }

                // remoulding core: CoreData columns 6/7/8 (attack, life, armor) x LevelUpData[core level] columns 3/4/5 / 1000
                uint core = RemouldingCores.OwnedCore(inputs.Gun);
                CoreData? coreData = core == 0 ? null : tables.GetTable<CoreData>().FirstOrDefault(row => TableFields.U(row, 1) == core);
                uint coreLevelId = RemouldingCores.LevelOf(inputs.Gun);
                LevelUpData? coreLevel = coreData is null ? null : tables.GetTable<LevelUpData>().FirstOrDefault(row => TableFields.U(row, 1) == coreLevelId);
                if (coreData is not null && coreLevel is not null)
                {
                    coreFlat[1] = CeilLike(TableFields.U(coreData, 6) * (TableFields.U(coreLevel, 3) / 1000.0));
                    coreFlat[2] = CeilLike(TableFields.U(coreData, 7) * (TableFields.U(coreLevel, 4) / 1000.0));
                    coreFlat[4] = CeilLike(TableFields.U(coreData, 8) * (TableFields.U(coreLevel, 5) / 1000.0));
                }

                // control room: ControlRoomData[room level] column 6 = a row with the stats of each duty (Guard/Scout/Assist/Suppress: GuardPow 201,
                // ScoutPow 203, ... GuardMaxHp 209 ..., GuardShieldArmor 217 ...) that apply to the characters of that duty
                uint duty = gun is null ? 0 : TableFields.U(gun, 13);
                ControlRoomData? room = inputs.ControlRoomLevel == 0 ? null : tables.GetTable<ControlRoomData>().FirstOrDefault(row => TableFields.U(row, 1) == inputs.ControlRoomLevel);
                if (room is not null && duty is >= 1 and <= 4 && Row(TableFields.U(room, 6)) is { } roomStats)
                {
                    int step = (int)(duty - 1) * 2;
                    controlRoomFlat[1] = Column(roomStats, 201 + step);
                    controlRoomFlat[2] = Column(roomStats, 209 + step);
                    controlRoomFlat[4] = Column(roomStats, 217 + step);
                }

                // stages whose first clear adds a row of "all characters" stats (DarkzoneHiddenStageBaseData column 5, columns AllAddPow/MaxHp/ShieldArmor)
                foreach (DarkzoneHiddenStageBaseData hidden in tables.GetTable<DarkzoneHiddenStageBaseData>())
                    if (inputs.PassedStages.Contains(TableFields.U(hidden, 1)) && Row(TableFields.U(hidden, 5)) is { } allAdd)
                        allAddRows.Add(allAdd);
            }

            uint weaponId = weapon?.WeaponId ?? DefaultWeaponOf(tables, gunId);
            GunWeaponData? weaponData = weaponId == 0 ? null : tables.GetTable<GunWeaponData>().FirstOrDefault(row => row.Id == weaponId);
            Dictionary<int, int>? weaponBase = null, weaponLevel = null, weaponBreak = null;
            if (weaponData is not null)
            {
                weaponBase = Row(TableFields.U(weaponData, 21));
                uint maxLevel = Math.Max(1u, TableFields.U(weaponData, 17));
                uint weaponLv = Math.Min((uint)Math.Max(1, weapon?.Level ?? 1), maxLevel);
                GunWeaponExpData? exp = tables.GetTable<GunWeaponExpData>().FirstOrDefault(row => row.Id == weaponLv * 100 + TableFields.U(weaponData, 11));
                weaponLevel = exp is null ? null : Row(TableFields.U(exp, 9));
                uint breaks = (uint)Math.Max(1, weapon?.BreakTimes ?? 1);
                if (breaks <= TableFields.U(weaponData, 18))
                {
                    WeaponBreakData? breakData = tables.GetTable<WeaponBreakData>().FirstOrDefault(row => row.Id == weaponId * 10 + breaks);
                    weaponBreak = breakData is null ? null : Row(TableFields.U(breakData, 5));
                }
            }

            // affixes of the parts (mods) on the weapon: GunWeaponMod field 7 holds four 16-bit pairs, low byte = value, high byte = DevelopProperty
            // (client LICHBCMONFI); the value is used as it is when the part is not polarized
            int Affixes(int property)
            {
                int sum = 0;
                foreach ((int affixProperty, int value) in inputs?.ModAffixes ?? [])
                    if (affixProperty == property)
                        sum += value;

                return sum;
            }

            int Weapon(int property) =>
                weaponBase is null ? 0 : Math.Abs(CeilLike(Column(weaponBase, property) * (Column(weaponLevel, property) / 1000.0)) + Column(weaponBreak, property) + Affixes(property));

            // the sources of a property: character (scaled by level) + classes + weapon + affection/keys; the flat value also takes the core and the control room
            int Sum(int property, bool flat) =>
                CeilLike(Column(baseRow, property) * (Column(levelRow, property) / 1000.0)) + classRows.Sum(row => Column(row, property)) + Weapon(property)
                + extraRows.Sum(row => Column(row, property))
                + keyValues.GetValueOrDefault(property)
                + (flat ? coreFlat.GetValueOrDefault(property) + controlRoomFlat.GetValueOrDefault(property) : 0);

            HashSet<int> properties = [];
            foreach (Dictionary<int, int>? row in new[] { baseRow, weaponBase, weaponBreak }.Concat(classRows).Concat(extraRows))
                if (row is not null)
                    foreach (int column in row.Keys)
                        if (ColumnToDevelopProperty.TryGetValue(column, out int property))
                            properties.Add(property);
            foreach (int property in coreFlat.Keys.Concat(controlRoomFlat.Keys).Concat(keyValues.Keys))
                properties.Add(property);
            foreach ((int affixProperty, _) in inputs?.ModAffixes ?? [])
                properties.Add(affixProperty);

            var developed = new Dictionary<int, int>();
            foreach (int property in properties)
            {
                int total = Sum(property, flat: true);
                if (PercentTwin.TryGetValue(property, out int twin))
                    total = CeilLike(total * (1.0 + Sum(twin, flat: false) / 1000.0));
                // all-characters rows (AllAddPow = 225, AllAddMaxHp = 226, AllAddShieldArmor = 227) come after the percentage
                int allAddProperty = property switch { 1 => 225, 2 => 226, 4 => 227, _ => 0 };
                if (allAddProperty != 0)
                    total += allAddRows.Sum(row => Column(row, allAddProperty));
                if (total != 0)
                    developed[property] = total;
            }

            developed[AlwaysOneProperty] = 1;
            return developed;
        }

        private static uint DefaultWeaponOf(ITableService tables, uint gunId)
        {
            GunData? gun = tables.GetTable<GunData>().FirstOrDefault(row => row.Id == gunId);
            return gun is null ? 0 : TableFields.List(gun, GunWeaponColumn).FirstOrDefault();
        }

        /// <summary>
        /// The skill list of a character at its grade: <c>GunGradeData</c> (id = gun id * 100 + grade + 1) column 11 is the comma
        /// separated list of the battle skills it has at that grade (<c>10170101,10170401,10170501,10170701</c>: normal attack plus
        /// the skills). Evidence: the client log of the 16:14 battle (<c>NullReferenceException</c> in <c>LGBAMOOLBKB.HBGLPAIEBLK</c>):
        /// that method treats the first 5 ids of the list as the main skills and looks every later id up in <c>BattleSkillData</c>
        /// without a null check. The earlier build sent <c>GunData</c> column 21 (those are the <c>GunGradeData</c> ids, 101701...,
        /// not skills) plus the grade row's column 4, so ids 6 and 7 were not in <c>BattleSkillData</c> and the unit never existed.
        /// ASSUMPTION: the column-11 list is what the official server sends as the unit's skills.
        /// </summary>
        /// <summary>
        /// The skills the plugins of the remoulding core switch on (client <c>FEIGCBDGLPB</c>): every plugin lists two affixes (<c>FeaturesEntryData</c> ids), each
        /// adds its value (column 12) to its group (column 8); for each group the skill (column 11) of the highest row whose value (column 12) does not
        /// exceed the sum is used. Only a character that owns its core has them (A-75).
        /// </summary>
        private static List<uint> CoreSkills(ITableService tables, StatInputs? inputs)
        {
            var skills = new List<uint>();
            if (inputs is null || inputs.PluginAffixes.Count == 0 || RemouldingCores.OwnedCore(inputs.Gun) == 0)
                return skills;

            List<FeaturesEntryData> entries = tables.GetTable<FeaturesEntryData>().ToList();
            var sums = new SortedDictionary<uint, uint>();
            foreach (uint affix in inputs.PluginAffixes)
                if (entries.FirstOrDefault(row => TableFields.U(row, 1) == affix) is { } entry)
                    sums[TableFields.U(entry, 8)] = sums.GetValueOrDefault(TableFields.U(entry, 8)) + TableFields.U(entry, 12);

            foreach ((uint group, uint total) in sums)
            {
                uint skill = entries.Where(row => TableFields.U(row, 8) == group && TableFields.U(row, 12) <= total)
                    .OrderByDescending(row => TableFields.U(row, 12)).Select(row => TableFields.U(row, 11)).FirstOrDefault();
                if (skill != 0 && !skills.Contains(skill))
                    skills.Add(skill);
            }

            // The masteries of the core (A-78): CoreData columns 9..14 name six MasterEntryData rows; as the client's panel decides ("Finish"), a mastery is reached when
            // the core level is at least its column 5 and the sums of the features by category (FeaturesEntryData column 7: 1 defense, 2 pioneer, 3 support, 4 power;
            // each affix adds its column 12) reach every number of its column 4. A reached mastery gives the battle skill of its column 6.
            var categories = new uint[5];
            foreach (uint affix in inputs.PluginAffixes)
                if (entries.FirstOrDefault(row => TableFields.U(row, 1) == affix) is { } featureEntry && TableFields.U(featureEntry, 7) is >= 1 and <= 4)
                    categories[TableFields.U(featureEntry, 7)] += TableFields.U(featureEntry, 12);

            uint core = RemouldingCores.OwnedCore(inputs.Gun);
            CoreData? coreData = tables.GetTable<CoreData>().FirstOrDefault(row => row.Id == core);
            uint coreLevel = Math.Max(1, RemouldingCores.LevelOf(inputs.Gun));
            if (coreData is not null)
                foreach (int column in new[] { 9, 10, 11, 12, 13, 14 })
                {
                    uint entryId = TableFields.U(coreData, column);
                    MasterEntryData? master = entryId == 0 ? null : tables.GetTable<MasterEntryData>().FirstOrDefault(row => TableFields.U(row, 1) == entryId);
                    if (master is null || TableFields.U(master, 5) > coreLevel)
                        continue;

                    IReadOnlyList<uint> needed = TableFields.List(master, 4);
                    bool reached = true;
                    for (int index = 0; index < needed.Count && index < 4; index++)
                        if (categories[index + 1] < needed[index])
                            reached = false;

                    uint masterSkill = TableFields.U(master, 6);
                    if (reached && masterSkill != 0 && !skills.Contains(masterSkill))
                        skills.Add(masterSkill);
                }

            return skills;
        }

        /// <summary>
        /// The skills of the keys the character wears (private slots = Gun field 42, public keys, peak keys = field 52), which the engine files under its
        /// "Talent" category (field 12 of the skill message): <c>TalentKeyData</c> column 6 = a <c>BattleSkillData</c> id (A-76).
        /// </summary>
        private static List<uint> KeySkills(ITableService tables, StatInputs? inputs)
        {
            var skills = new List<uint>();
            if (inputs is null)
                return skills;

            IEnumerable<uint> keys = inputs.Gun.PrivateTalentSkillItems.Concat(inputs.PublicKeys.Select(k => k.Stc)).Concat(TalentKeySlots.Normalize(inputs.Gun)).Where(k => k != 0);
            foreach (uint key in keys)
            {
                TalentKeyData? row = tables.GetTable<TalentKeyData>().FirstOrDefault(r => TableFields.U(r, 1) == key);
                uint skill = row is null ? 0 : TableFields.U(row, 6);
                if (skill != 0 && !skills.Contains(skill))
                    skills.Add(skill);
            }

            return skills;
        }

        /// <summary>A list column of <c>ReplaceSkillData</c> (one value is stored as a plain number).</summary>
        private static IReadOnlyList<uint> ReplaceList(ReplaceSkillData row, int column)
        {
            IReadOnlyList<uint> list = TableFields.List(row, column);
            return list.Count > 0 ? list : TableFields.U(row, column) is var single and not 0 ? [single] : [];
        }

        private static byte[] SkillMessage(ITableService tables, uint gunId, uint grade, uint weaponId = 0, uint breaks = 1, IReadOnlyList<uint>? coreSkills = null, IReadOnlyList<uint>? keySkills = null, IReadOnlyList<(uint Skill, int[] Values)>? accessories = null, IReadOnlyList<uint>? privateKeys = null)
        {
            GunGradeData? gradeRow = tables.GetTable<GunGradeData>().FirstOrDefault(row => row.Id == gunId * 100 + grade + 1)
                ?? tables.GetTable<GunGradeData>().FirstOrDefault(row => row.Id == gunId * 100 + 1);
            // The character's skills as the client's own unit builder lists them (FJLGHAHLHML.OLANPMPHOJE, A-86): the basic attack (first skill of the first grade row), then
            // for every grade row up to the current one the skills of GunGradeData column 4 ("abbr"; item / 100 is the skill slot and the highest version of each slot wins; 90909 is
            // ignored), ordered by BattleSkillDisplayData column 7 and the id. Column 11 (the skills of the current grade) lacks the passive of the character
            // (e.g. 10170801), so passives such as the poison zone of Besti never started.
            var skills = new List<uint>();
            GunGradeData? firstRow = tables.GetTable<GunGradeData>().FirstOrDefault(row => row.Id == gunId * 100 + 1);
            if (firstRow is not null && uint.TryParse(TableFields.S(firstRow, GradeSkillSetColumn).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(), out uint basicSkill) && basicSkill != 0)
                skills.Add(basicSkill);

            var bySlot = new Dictionary<uint, uint>();
            for (uint index = 1; index <= grade + 1; index++)
            {
                GunGradeData? stepRow = tables.GetTable<GunGradeData>().FirstOrDefault(row => row.Id == gunId * 100 + index);
                if (stepRow is null)
                    continue;

                foreach (uint item in TableFields.List(stepRow, 4))
                    if (item != 90909 && (!bySlot.TryGetValue(item / 100, out uint known) || known < item))
                        bySlot[item / 100] = item;
            }

            foreach (uint skill in bySlot.Values)
                if (!skills.Contains(skill))
                    skills.Add(skill);

            // when the table has no abbr list at all, fall back to the skills of the current grade row
            if (skills.Count <= 1)
                foreach (string part in (gradeRow is null ? string.Empty : TableFields.S(gradeRow, GradeSkillSetColumn)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (uint.TryParse(part, out uint skill) && skill != 0 && !skills.Contains(skill))
                        skills.Add(skill);

            // Private keys of type 1 replace skills (client HFHFEIBOADO/OACGDMNAAFM, A-87): TalentKeyData column 14 names a ReplaceSkillData row whose four pairs of columns
            // (2/3, 4/5, 6/7, 8/9) are "skills to remove" and "skills to put instead": when something was removed, the first skill of the second list is added.
            foreach (uint key in privateKeys ?? [])
            {
                TalentKeyData? keyRow = key == 0 ? null : tables.GetTable<TalentKeyData>().FirstOrDefault(row => TableFields.U(row, 1) == key);
                uint replaceId = keyRow is null || TableFields.U(keyRow, 2) != 1 ? 0 : TableFields.U(keyRow, 14);
                ReplaceSkillData? replace = replaceId == 0 ? null : tables.GetTable<ReplaceSkillData>().FirstOrDefault(row => TableFields.U(row, 1) == replaceId);
                if (replace is null)
                    continue;

                foreach (int column in new[] { 2, 4, 6, 8 })
                {
                    bool removed = false;
                    foreach (uint old in ReplaceList(replace, column))
                        removed |= skills.Remove(old);
                    IReadOnlyList<uint> added = ReplaceList(replace, column + 1);
                    if (removed && added.Count > 0)
                        skills.Add(added[0]);
                }
            }

            Dictionary<uint, uint> displayOrder = [];
            foreach (BattleSkillDisplayData display in tables.GetTable<BattleSkillDisplayData>().Where(row => skills.Contains(TableFields.U(row, 1))))
                displayOrder[TableFields.U(display, 1)] = TableFields.U(display, 7);
            skills = skills.OrderBy(skill => displayOrder.GetValueOrDefault(skill)).ThenBy(skill => skill).ToList();

            // The grade passives (column 5 = add_abbr) follow the main skills: the client treats every id after the fifth as an extra
            // passive/active and they are what sets up the character's mechanics. Without them 莱妮 (1058) summoned a drone with mirage id 0
            // (her grade row sets the variable that selects it, e.g. buff 10580497 = variable 10580401 = 587): the log showed
            // "BattleMirageData id 0 not found" and the unit never finished its turn, so the automatic mode stalled after her.
            if (gradeRow is not null)
                foreach (uint extra in TableFields.List(gradeRow, GradeExtraSkillsColumn))
                    if (extra != 0 && !skills.Contains(extra))
                        skills.Add(extra);

            // The weapon (A-75), as the client's own unit builder writes it: field 9 = the weapon skill (BattleWeaponData column 2, replaced by column 4
            // of the WeaponBreakData row of its break when that one names a skill), field 13 = the extra skill of the character's own weapon
            // (BattleWeaponData column 4), field 16 = the weapon passives (BattleWeaponData column 5).
            uint weaponSkill = 0, exclusiveSkill = 0;
            List<uint> weaponPassives = [];
            BattleWeaponData? battleWeapon = weaponId == 0 ? null : tables.GetTable<BattleWeaponData>().FirstOrDefault(row => row.Id == weaponId);
            if (battleWeapon is not null)
            {
                weaponSkill = TableFields.U(battleWeapon, 2);
                WeaponBreakData? breakRow = tables.GetTable<WeaponBreakData>().FirstOrDefault(row => row.Id == weaponId * 10 + breaks);
                if (breakRow is not null && TableFields.U(breakRow, 4) != 0)
                    weaponSkill = TableFields.U(breakRow, 4);

                GunData? gun = tables.GetTable<GunData>().FirstOrDefault(row => row.Id == gunId);
                if (gun is not null && TableFields.List(gun, GunWeaponColumn).Contains(weaponId))
                    exclusiveSkill = TableFields.U(battleWeapon, 4);

                weaponPassives = TableFields.List(battleWeapon, 5).Where(id => id != 0).Distinct().ToList();
                if (weaponPassives.Count == 0 && TableFields.U(battleWeapon, 5) != 0)
                    weaponPassives.Add(TableFields.U(battleWeapon, 5));
            }

            return Encode(output =>
            {
                if (skills.Count > 0)
                {
                    byte[] packed = Encode(inner =>
                    {
                        foreach (uint skill in skills)
                            inner.WriteUInt32(skill);
                    });
                    WriteBytes(output, 6, packed);
                }

                if (coreSkills is { Count: > 0 })
                    WriteBytes(output, 11, Encode(inner =>
                    {
                        foreach (uint skill in coreSkills)
                            inner.WriteUInt32(skill);
                    }));

                // field 15: the powers of the mods (skill, base values, extra values) that the battle expressions read as the weapon-part specification (A-78)
                if (accessories is { Count: > 0 })
                    foreach ((uint accessorySkill, int[] values) in accessories)
                        WriteBytes(output, 15, Encode(inner =>
                        {
                            inner.WriteTag(1, WireFormat.WireType.Varint);
                            inner.WriteUInt32(accessorySkill);
                            WriteBytes(inner, 2, Encode(packed => { foreach (int value in values) packed.WriteInt32(value); }));
                            WriteBytes(inner, 3, Encode(packed => { foreach (int value in values) packed.WriteInt32(0); }));
                        }));

                if (keySkills is { Count: > 0 })
                    WriteBytes(output, 12, Encode(inner =>
                    {
                        foreach (uint skill in keySkills)
                            inner.WriteUInt32(skill);
                    }));

                if (weaponSkill != 0)
                {
                    output.WriteTag(9, WireFormat.WireType.Varint);
                    output.WriteUInt32(weaponSkill);
                }

                if (exclusiveSkill != 0)
                {
                    output.WriteTag(13, WireFormat.WireType.Varint);
                    output.WriteUInt32(exclusiveSkill);
                }

                if (weaponPassives.Count > 0)
                    WriteBytes(output, 16, Encode(inner =>
                    {
                        foreach (uint passive in weaponPassives)
                            inner.WriteUInt32(passive);
                    }));
            });
        }

        private static byte[] PropertyMap(IReadOnlyDictionary<int, int> stats) => Encode(output =>
        {
            foreach ((int key, int value) in stats.OrderBy(pair => pair.Key))
            {
                byte[] entry = Encode(inner =>
                {
                    inner.WriteTag(1, WireFormat.WireType.Varint);
                    inner.WriteInt32(key);
                    inner.WriteTag(2, WireFormat.WireType.Varint);
                    inner.WriteInt32(value);
                });
                WriteBytes(output, 1, entry);
            }
        });

        /// <summary>
        /// Every <c>PropertyData</c> row as column to value (read from the serialized row, so columns the stale generated type does not
        /// declare are included). The table has ~150k rows; it is read once.
        /// </summary>
        private static Dictionary<uint, Dictionary<int, int>> StatRows(ITableService tables)
        {
            lock (StatRowsLock)
            {
                if (statRows is not null)
                    return statRows;

                var rows = new Dictionary<uint, Dictionary<int, int>>();
                foreach (PropertyData row in tables.GetTable<PropertyData>())
                {
                    var input = new CodedInputStream(row.ToByteArray());
                    uint id = 0;
                    var columns = new Dictionary<int, int>();
                    uint tag;
                    while ((tag = input.ReadTag()) != 0)
                    {
                        int number = WireFormat.GetTagFieldNumber(tag);
                        if (WireFormat.GetTagWireType(tag) != WireFormat.WireType.Varint)
                        {
                            input.SkipLastField();
                            continue;
                        }

                        long value = input.ReadInt64();
                        if (number == 1)
                            id = (uint)value;
                        else if (number <= LastStatColumn && value != 0)
                            columns[number] = (int)value;
                    }

                    if (id != 0)
                        rows[id] = columns;
                }

                return statRows = rows;
            }
        }

        private static byte[] Encode(Action<CodedOutputStream> write)
        {
            using var stream = new MemoryStream();
            using (var output = new CodedOutputStream(stream, leaveOpen: true))
                write(output);
            return stream.ToArray();
        }

        private static void WriteBytes(CodedOutputStream output, int number, byte[] value)
        {
            output.WriteTag(number, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(value));
        }
    }
}
