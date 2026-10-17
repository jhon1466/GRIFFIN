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
        public static byte[] GunUnit(ITableService tables, uint pointId, uint gunId, Gun character, WeaponEntity? weapon = null, string rotation = DefaultRotation)
        {
            byte[] gunUnit = Encode(output =>
            {
                WriteBytes(output, 1, WithBattleData(tables, gunId, character, weapon).ToByteArray());
                output.WriteTag(2, WireFormat.WireType.Varint);
                output.WriteUInt32(pointId);
                WriteBytes(output, 3, System.Text.Encoding.UTF8.GetBytes(rotation));
                WriteBytes(output, 10, []);
                WriteBytes(output, 11, SkillMessage(tables, gunId, character.Grade));
                WriteBytes(output, 12, []);
                WriteBytes(output, 22, PropertyMap(StatsOf(tables, gunId, weapon?.WeaponId ?? DefaultWeaponOf(tables, gunId))));
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
                WeaponLite = weapon?.ToProtoWeapon() ?? new GunWeaponLite { StcId = weaponStcId, Level = 1, GunId = gunId, BreakTimes = 1 },
            };
#pragma warning restore CS0612, CS0618
            return unit;
        }

        /// <summary>
        /// The stat map of a character, keyed by <c>DevelopProperty</c>: the nonzero columns of its own <c>PropertyData</c> row (<c>GunData</c> column 40, e.g. 11017 for
        /// gun 1017; every such row is a complete set, rates such as the critical multiplier included, so rows are not summed) plus the
        /// attack of its weapon's row (the weapon's stc id is also a <c>PropertyData</c> id), and the flag the client always sets.
        /// Level, class and module scaling are not applied.
        /// </summary>
        public static IReadOnlyDictionary<int, int> StatsOf(ITableService tables, uint gunId, uint weaponStcId = 0)
        {
            var stats = new Dictionary<int, int>();
            GunData? gun = tables.GetTable<GunData>().FirstOrDefault(row => row.Id == gunId);
            Dictionary<uint, Dictionary<int, int>> rows = StatRows(tables);
            uint own = gun is null ? 0 : TableFields.U(gun, GunPropertyColumn);
            if (own != 0 && rows.TryGetValue(own, out Dictionary<int, int>? ownRow))
            {
                foreach ((int column, int value) in ownRow)
                    stats[column] = value;
            }

            if (weaponStcId != 0 && rows.TryGetValue(weaponStcId, out Dictionary<int, int>? weaponRow))
                stats[AttackColumn] = stats.GetValueOrDefault(AttackColumn) + weaponRow.GetValueOrDefault(AttackColumn);

            var developed = new Dictionary<int, int>();
            foreach ((int column, int value) in stats)
            {
                if (ColumnToDevelopProperty.TryGetValue(column, out int property))
                    developed[property] = value;
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
        private static byte[] SkillMessage(ITableService tables, uint gunId, uint grade)
        {
            GunGradeData? gradeRow = tables.GetTable<GunGradeData>().FirstOrDefault(row => row.Id == gunId * 100 + grade + 1)
                ?? tables.GetTable<GunGradeData>().FirstOrDefault(row => row.Id == gunId * 100 + 1);
            var skills = new List<uint>();
            foreach (string part in (gradeRow is null ? string.Empty : TableFields.S(gradeRow, GradeSkillSetColumn)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (uint.TryParse(part, out uint skill) && skill != 0 && !skills.Contains(skill))
                    skills.Add(skill);

            // The grade passives (column 5 = add_abbr) follow the main skills: the client treats every id after the fifth as an extra
            // passive/active and they are what sets up the character's mechanics. Without them 莱妮 (1058) summoned a drone with mirage id 0
            // (her grade row sets the variable that selects it, e.g. buff 10580497 = variable 10580401 = 587): the log showed
            // "BattleMirageData id 0 not found" and the unit never finished its turn, so the automatic mode stalled after her.
            if (gradeRow is not null)
                foreach (uint extra in TableFields.List(gradeRow, GradeExtraSkillsColumn))
                    if (extra != 0 && !skills.Contains(extra))
                        skills.Add(extra);

            return Encode(output =>
            {
                if (skills.Count == 0)
                    return;

                byte[] packed = Encode(inner =>
                {
                    foreach (uint skill in skills)
                        inner.WriteUInt32(skill);
                });
                WriteBytes(output, 6, packed);
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
