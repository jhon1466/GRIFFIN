using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed record LevelUpResult(uint GunId, uint Level, uint Exp, Common.Proto.SC_ResourceUpdate Spent);

    public sealed record ClassUpResult(uint GunClass, List<Google.Protobuf.IMessage> Spent);

    /// <summary>
    /// Character level, class (promotion) and grade, stored in <see cref="GunEntity"/>.
    /// Level-up is paid with resource 25 (the "战场报告" battle reports, the character EXP currency) and resource 2 (gold, the
    /// "gold" column of <c>GunLevelExpData</c>), using the per-level costs of that table. The level cap comes from the class:
    /// class 1..5 allow 20/30/40/50/60 (the <c>gun_level_max</c> column of every <c>GunClassData</c> row follows that rule).
    /// Class promotion charges the materials of <c>GunClassData</c> (group = <c>GunData</c> column 35). Assumption: grade (star)
    /// upgrades are free (the cost is not in the client tables).
    /// </summary>
    public interface ICharacterProgressService : IGameService
    {
        const uint ExpResource = 25;
        const uint GoldResource = 2;
        const uint MaxLevel = 60;
        const uint MaxClass = 5;

        /// <summary>Why the last refused call (null result) was refused; scoped per request.</summary>
        HNINDAOJCKP LastError { get; }

        uint LevelCap(uint gunClass);

        /// <summary>Raises the character to <paramref name="targetLevel"/>; null when it is not possible (unknown, capped, or not enough battle reports/gold).</summary>
        LevelUpResult? LevelUp(uint accountUid, uint gunId, uint targetLevel);

        /// <summary>Promotes the character to the next class: requires the class cap level and charges the materials of <c>GunClassData</c>. Null when not possible.</summary>
        ClassUpResult? ClassUp(uint accountUid, uint gunId);

        /// <summary>Raises the grade by one (free); returns the new grade or null for an unknown character.</summary>
        uint? GradeUp(uint accountUid, uint gunId);
    }

    public sealed class CharacterProgressService(
        IAccountService accountService,
        IGunService gunService,
        IResourceService resourceService,
        IItemUseService itemUse,
        ITableService tableService) : ICharacterProgressService
    {
        private const uint MaxGrade = 6;

        public HNINDAOJCKP LastError { get; private set; }

        private T? Fail<T>(HNINDAOJCKP error) where T : class
        {
            LastError = error;
            return null;
        }

        public uint LevelCap(uint gunClass) =>
            gunClass >= ICharacterProgressService.MaxClass ? ICharacterProgressService.MaxLevel : 10 + 10 * gunClass;

        public LevelUpResult? LevelUp(uint accountUid, uint gunId, uint targetLevel)
        {
            GunEntity? gun = Find(accountUid, gunId);
            if (gun is null)
                return Fail<LevelUpResult>(HNINDAOJCKP.GunNotFound);

            uint cap = LevelCap(gun.GunClass == 0 ? 1 : gun.GunClass);
            uint target = Math.Min(targetLevel, cap);
            if (target <= (uint)gun.Level)
            {
                Log.Information("LevelUp: gun {GunId} is already level {Level} (target {Target}, class cap {Cap}).", gunId, gun.Level, targetLevel, cap);
                return Fail<LevelUpResult>(HNINDAOJCKP.GunLevelMax);
            }

            Dictionary<uint, GunLevelExpData> rows = tableService.GetTable<GunLevelExpData>().ToDictionary(row => row.Level);
            long exp = 0;
            long gold = 0;
            for (uint level = (uint)gun.Level + 1; level <= target; level++)
            {
                if (!rows.TryGetValue(level, out GunLevelExpData? row))
                    continue;

                exp += row.Exp;
                gold += row.Gold;
            }

            exp = Math.Max(0, exp - gun.Exp);
            Dictionary<uint, int> cost = new();
            if (exp > 0) cost[ICharacterProgressService.ExpResource] = (int)Math.Min(exp, int.MaxValue);
            if (gold > 0) cost[ICharacterProgressService.GoldResource] = (int)Math.Min(gold, int.MaxValue);

            if (!resourceService.TrySpend(accountUid, cost, out Common.Proto.SC_ResourceUpdate spent))
            {
                Log.Warning("LevelUp: account {Uid} cannot pay {Cost} to take gun {GunId} to level {Level}.", accountUid,
                    string.Join(",", cost.Select(pair => $"{pair.Key}x{pair.Value}")), gunId, target);
                return Fail<LevelUpResult>(HNINDAOJCKP.ItemNotEnough);
            }

            gun.Level = (int)target;
            gun.Exp = 0;
            gunService.Save();
            Log.Information("LevelUp: account {Uid} gun {GunId} -> level {Level} for {Cost}.", accountUid, gunId, target,
                string.Join(",", cost.Select(pair => $"{pair.Key}x{pair.Value}")));
            return new LevelUpResult(gunId, target, 0, spent);
        }

        public ClassUpResult? ClassUp(uint accountUid, uint gunId)
        {
            GunEntity? gun = Find(accountUid, gunId);
            if (gun is null)
                return Fail<ClassUpResult>(HNINDAOJCKP.GunNotFound);

            uint gunClass = gun.GunClass == 0 ? 1 : gun.GunClass;
            if (gunClass >= ICharacterProgressService.MaxClass || (uint)gun.Level < LevelCap(gunClass))
            {
                Log.Information("ClassUp: gun {GunId} (class {Class}, level {Level}) is not ready for promotion.", gunId, gunClass, gun.Level);
                return Fail<ClassUpResult>(gunClass >= ICharacterProgressService.MaxClass ? HNINDAOJCKP.GunClassMax : HNINDAOJCKP.GunLevelNotGradeMax);
            }

            // GunData column 35 = class group; GunClassData: 2 = group, 3 = class, 7 = materials to leave that class
            GunData? data = tableService.GetTable<GunData>().FirstOrDefault(g => g.Id == gunId);
            uint group = data is null ? 0 : TableFields.U(data, 35);
            GunClassData? row = tableService.GetTable<GunClassData>().FirstOrDefault(c => TableFields.U(c, 2) == group && TableFields.U(c, 3) == gunClass);
            Dictionary<uint, int> cost = row is null ? [] : TableFields.Map(row, 7).ToDictionary(pair => pair.Key, pair => (int)pair.Value);
            List<Google.Protobuf.IMessage> spent = [];
            if (!itemUse.Spend(accountUid, cost, spent))
            {
                Log.Warning("ClassUp: account {Uid} cannot pay {Cost} for gun {GunId}.", accountUid, string.Join(",", cost.Select(pair => $"{pair.Key}x{pair.Value}")), gunId);
                return Fail<ClassUpResult>(HNINDAOJCKP.ItemNotEnough);
            }

            gun.GunClass = gunClass + 1;
            gunService.Save();
            Log.Information("ClassUp: account {Uid} gun {GunId} -> class {Class} for {Cost}.", accountUid, gunId, gun.GunClass,
                string.Join(",", cost.Select(pair => $"{pair.Key}x{pair.Value}")));
            return new ClassUpResult(gun.GunClass, spent);
        }

        public uint? GradeUp(uint accountUid, uint gunId)
        {
            GunEntity? gun = Find(accountUid, gunId);
            if (gun is null)
            {
                LastError = HNINDAOJCKP.GunNotFound;
                return null;
            }

            if (gun.Grade < MaxGrade)
            {
                gun.Grade++;
                gunService.Save();
            }

            return gun.Grade;
        }

        private GunEntity? Find(uint accountUid, uint gunId) =>
            accountService.GetByUid(accountUid)?.Guns.FirstOrDefault(g => g.GunId == gunId);
    }
}
