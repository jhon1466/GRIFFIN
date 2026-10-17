using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed record ModLevelResult(uint Id, uint Level, uint Exp);

    /// <summary>
    /// Weapon mod (配件) levelling and dismantling, read by field number from client 1189260:
    /// <c>WeaponModData</c> 3 = rank, 4 = quality, 9 = base experience the mod is worth as a material;
    /// <c>ModExpData</c> (rank, quality, level) → 5 = experience of that level, 6 = accumulated experience to reach it, 7 = gold per
    /// experience; <c>ModExpItemData</c> (item 8307) = experience of one upgrade item. The level cap of a rank is its last row
    /// (rank 1-3: 10, 4: 15, 5: 20). Assumptions: the experience stored in the mod is the progress inside the level (as for
    /// characters), gold is charged as <c>experience × money</c>, a material mod is worth its base experience plus what was
    /// invested in it, and dismantling returns upgrade items worth the same experience.
    /// </summary>
    public interface IModProgressService : IGameService
    {
        /// <summary>Why the last refused call was refused; scoped per request.</summary>
        HNINDAOJCKP LastError { get; }

        ModLevelResult? LevelUp(uint uid, uint modId, IEnumerable<uint> materials, IReadOnlyDictionary<uint, uint> items, List<IMessage> messages);

        IReadOnlyList<uint> Dismantle(uint uid, IEnumerable<uint> modIds, List<IMessage> messages);
    }

    public sealed class ModProgressService(
        IAccountService accountService,
        IWeaponModService weaponMods,
        ITableService tableService,
        IItemUseService itemUse,
        IRewardService rewardService) : IModProgressService
    {
        private const uint GoldResource = 2;

        public HNINDAOJCKP LastError { get; private set; }

        private ModLevelResult? Fail(HNINDAOJCKP error)
        {
            LastError = error;
            return null;
        }

        private const uint UpgradeItem = 8307;

        public ModLevelResult? LevelUp(uint uid, uint modId, IEnumerable<uint> materials, IReadOnlyDictionary<uint, uint> items, List<IMessage> messages)
        {
            AccountEntity? account = accountService.GetByUid(uid);
            WeaponModEntity? mod = account?.WeaponMods.FirstOrDefault(m => m.Id == modId);
            if (account is null || mod is null)
                return Fail(HNINDAOJCKP.GunWeaponModNotFound);

            if (Curve(mod) is not { Count: > 0 } curve)
                return Fail(HNINDAOJCKP.GunWeaponModExpTableCanNotFound);

            uint cap = curve[^1].Level;
            if (Math.Max(1, mod.Level) >= cap)
                return Fail(HNINDAOJCKP.GunWeaponModMaxLevel);

            long gained = 0;
            List<WeaponModEntity> consumed = [];
            foreach (uint materialId in materials.Distinct().Where(id => id != modId))
            {
                WeaponModEntity? material = account.WeaponMods.FirstOrDefault(m => m.Id == materialId);
                if (material is null)
                    return Fail(HNINDAOJCKP.GunWeaponModMaterialNotFound);

                if (material.Field5 != 0 || Equipped(account, material.Id))
                    return Fail(HNINDAOJCKP.GunWeaponModMaterialNotFree);

                gained += BaseExp(material) + Invested(material);
                consumed.Add(material);
            }

            long perItem = tableService.GetTable<ModExpItemData>().Where(i => TableFields.U(i, 1) == UpgradeItem).Select(i => (long)TableFields.U(i, 2)).FirstOrDefault();
            Dictionary<uint, int> cost = [];
            foreach ((uint item, uint count) in items)
            {
                if (item != UpgradeItem || count == 0)
                    return Fail(HNINDAOJCKP.GunWeaponLvUpUsedWrongItem);

                gained += perItem * count;
                cost[item] = (int)count;
            }

            if (gained <= 0)
                return Fail(HNINDAOJCKP.ClientInputInvalid);

            long total = Invested(mod) + gained;
            long limit = curve[^1].Total;
            long applied = Math.Min(total, limit) - Invested(mod);
            uint money = tableService.GetTable<ModExpData>().Select(r => TableFields.U(r, 7)).FirstOrDefault(m => m > 0);
            cost[GoldResource] = (int)Math.Min(int.MaxValue, applied * Math.Max(1, money));

            List<IMessage> spent = [];
            if (!itemUse.Spend(uid, cost, spent))
                return Fail(HNINDAOJCKP.ItemNotEnough);

            foreach (WeaponModEntity material in consumed)
                weaponMods.RemoveWeaponMod(uid, material);

            Set(mod, curve, Math.Min(total, limit));
            weaponMods.Save();
            messages.AddRange(spent);
            Log.Information("Mod {Mod} of account {Uid}: +{Gained} experience -> level {Level} ({Exp}).", modId, uid, gained, mod.Level, mod.Field6);
            return new ModLevelResult(mod.Id, mod.Level, mod.Field6);
        }

        public IReadOnlyList<uint> Dismantle(uint uid, IEnumerable<uint> modIds, List<IMessage> messages)
        {
            AccountEntity? account = accountService.GetByUid(uid);
            if (account is null)
                return [];

            long perItem = tableService.GetTable<ModExpItemData>().Where(i => TableFields.U(i, 1) == UpgradeItem).Select(i => (long)TableFields.U(i, 2)).FirstOrDefault();
            List<uint> removed = [];
            long experience = 0;
            foreach (uint id in modIds.Distinct())
            {
                WeaponModEntity? mod = account.WeaponMods.FirstOrDefault(m => m.Id == id);
                if (mod is null || mod.Field5 != 0 || Equipped(account, mod.Id))
                    continue;

                experience += BaseExp(mod) + Invested(mod);
                weaponMods.RemoveWeaponMod(uid, mod);
                removed.Add(id);
            }

            if (removed.Count > 0 && perItem > 0 && experience / perItem > 0)
                rewardService.Grant(uid, UpgradeItem, (int)(experience / perItem), messages);
            return removed;
        }

        private static bool Equipped(AccountEntity account, uint modId) => account.Weapons.Any(w => w.EquippedModIds.Contains(modId));

        private WeaponModData? Data(WeaponModEntity mod) => tableService.GetTable<WeaponModData>().FirstOrDefault(d => d.Id == mod.WeaponModId);

        private long BaseExp(WeaponModEntity mod) => Data(mod) is { } data ? TableFields.U(data, 9) : 0;

        /// <summary>(level, accumulated experience to reach it) of the mod's rank and quality, ordered by level.</summary>
        private List<(uint Level, long Total)> Curve(WeaponModEntity mod)
        {
            WeaponModData? data = Data(mod);
            if (data is null)
                return [];

            uint rank = TableFields.U(data, 3);
            uint quality = TableFields.U(data, 4);
            return tableService.GetTable<ModExpData>()
                .Where(r => TableFields.U(r, 2) == rank && TableFields.U(r, 3) == quality)
                .Select(r => (Level: TableFields.U(r, 4), Total: (long)TableFields.U(r, 6)))
                .OrderBy(p => p.Level)
                .ToList();
        }

        /// <summary>Experience invested in the mod: accumulated experience of its level plus the progress inside it.</summary>
        private long Invested(WeaponModEntity mod)
        {
            List<(uint Level, long Total)> curve = Curve(mod);
            uint level = Math.Max(1, mod.Level);
            return curve.FirstOrDefault(p => p.Level == level).Total + mod.Field6;
        }

        private static void Set(WeaponModEntity mod, List<(uint Level, long Total)> curve, long total)
        {
            (uint level, long floor) = curve.Last(p => p.Total <= total);
            mod.Level = level;
            mod.Field6 = (uint)(total - floor);
        }
    }
}
