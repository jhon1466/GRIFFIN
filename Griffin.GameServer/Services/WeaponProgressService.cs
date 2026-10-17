using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed record WeaponLevelResult(uint WeaponUid, uint Level, uint Exp, IReadOnlyList<IMessage> Updates);

    /// <summary>
    /// Weapon level, lock, dismantle and break. Verified against the 1189260 data:
    /// <c>GunWeaponData</c> field 11 = rank (3/4/5), 17 = max level (60), 20 = sell value, 22 = dismantle product (the weapon's
    /// blueprint item x60); <c>GunWeaponExpData</c> rows are keyed level*100+rank and give the cumulative exp (7) and gold (8)
    /// up to that level plus the item (10) that carries the exp (8301 "解析图纸").
    /// Assumptions: one unit of that item is one exp point; the break count (max 5) does not limit the level; locking uses
    /// bit 0 of <c>WeaponEntity.Flags</c>.
    /// </summary>
    public interface IWeaponProgressService : IGameService
    {
        const uint GoldResource = 2;
        const uint LockFlag = 1;
        const uint MaxBreak = 5;

        /// <summary>Why the last refused call was refused; scoped per request.</summary>
        Common.Proto.HNINDAOJCKP LastError { get; }

        WeaponLevelResult? LevelUp(uint accountUid, uint weaponUid, uint targetLevel);

        /// <summary>Dismantles unlocked, unequipped weapons; returns the uids that were removed and appends the grant messages.</summary>
        IReadOnlyList<uint> Dismantle(uint accountUid, IEnumerable<uint> weaponUids, List<IMessage> messages);

        bool? ToggleLock(uint accountUid, uint weaponUid);

        /// <summary>Breaks the weapon using a duplicate (same weapon id, unlocked, unequipped) as the material.</summary>
        uint? Break(uint accountUid, uint weaponUid, uint materialUid);
    }

    public sealed class WeaponProgressService(
        IAccountService accountService,
        IWeaponService weaponService,
        IInventoryService inventoryService,
        IResourceService resourceService,
        IItemService itemService,
        IRewardService rewardService,
        ITableService tableService) : IWeaponProgressService
    {
        public Common.Proto.HNINDAOJCKP LastError { get; private set; }

        private WeaponLevelResult? Fail(Common.Proto.HNINDAOJCKP error)
        {
            LastError = error;
            return null;
        }

        public WeaponLevelResult? LevelUp(uint accountUid, uint weaponUid, uint targetLevel)
        {
            WeaponEntity? weapon = Find(accountUid, weaponUid);
            GunWeaponData? data = weapon is null ? null : tableService.GetTable<GunWeaponData>().FirstOrDefault(w => w.Id == weapon.WeaponId);
            if (weapon is null || data is null)
                return Fail(Common.Proto.HNINDAOJCKP.GunWeaponNotFound);

            uint cap = TableFields.U(data, 17);
            uint rank = TableFields.U(data, 11);
            uint target = Math.Min(targetLevel, cap == 0 ? 60 : cap);
            if (target <= (uint)weapon.Level)
                return Fail(Common.Proto.HNINDAOJCKP.GunWeaponMaxLevel);

            Dictionary<uint, GunWeaponExpData> rows = tableService.GetTable<GunWeaponExpData>().ToDictionary(row => row.Id);
            if (!rows.TryGetValue(target * 100 + rank, out GunWeaponExpData? to))
                return Fail(Common.Proto.HNINDAOJCKP.GunWeaponLevelUpConfigNotExist);

            rows.TryGetValue((uint)weapon.Level * 100 + rank, out GunWeaponExpData? from);
            long exp = Math.Max(0, (long)TableFields.U(to, 7) - (from is null ? 0 : TableFields.U(from, 7)) - weapon.CurExp);
            long gold = Math.Max(0, (long)TableFields.U(to, 8) - (from is null ? 0 : TableFields.U(from, 8)));
            uint expItem = TableFields.U(to, 10);

            Dictionary<uint, int> itemCost = exp > 0 && expItem != 0 ? new() { [expItem] = (int)Math.Min(exp, int.MaxValue) } : new();
            Dictionary<uint, int> goldCost = gold > 0 ? new() { [IWeaponProgressService.GoldResource] = (int)Math.Min(gold, int.MaxValue) } : new();

            if (!itemService.TrySpend(accountUid, itemCost, out SC_Items itemUpdate))
            {
                Log.Warning("Weapon level-up: account {Uid} lacks {Cost} to take weapon {Uid2} to level {Level}.", accountUid,
                    string.Join(",", itemCost.Select(pair => $"{pair.Key}x{pair.Value}")), weaponUid, target);
                return Fail(Common.Proto.HNINDAOJCKP.ItemNotEnough);
            }

            if (!resourceService.TrySpend(accountUid, goldCost, out SC_ResourceUpdate goldUpdate))
            {
                // give the items back: nothing is charged unless both parts can be paid
                foreach ((uint id, int amount) in itemCost)
                    itemService.Grant(accountUid, id, 4, amount);

                Log.Warning("Weapon level-up: account {Uid} lacks the gold to take weapon {Weapon} to level {Level}.", accountUid, weaponUid, target);
                return Fail(Common.Proto.HNINDAOJCKP.ItemNotEnough);
            }

            weapon.Level = (int)target;
            weapon.CurExp = 0;
            weaponService.Save();
            Log.Information("Weapon level-up: account {Uid} weapon {Weapon} -> level {Level}.", accountUid, weaponUid, target);

            List<IMessage> updates = [];
            if (itemUpdate.Items.Count > 0) updates.Add(itemUpdate);
            if (goldUpdate.ResourceEx.Count > 0) updates.Add(goldUpdate);
            return new WeaponLevelResult(weaponUid, target, 0, updates);
        }

        public IReadOnlyList<uint> Dismantle(uint accountUid, IEnumerable<uint> weaponUids, List<IMessage> messages)
        {
            AccountEntity account = accountService.GetByUid(accountUid)!;
            List<uint> removed = [];

            foreach (uint uid in weaponUids.Distinct())
            {
                WeaponEntity? weapon = account.Weapons.FirstOrDefault(w => w.Id == uid);
                if (weapon is null || weapon.GunId != 0 || (weapon.Flags & IWeaponProgressService.LockFlag) != 0)
                    continue;

                GunWeaponData? data = tableService.GetTable<GunWeaponData>().FirstOrDefault(w => w.Id == weapon.WeaponId);
                if (data is null)
                    continue;

                inventoryService.Remove(accountUid, weapon);
                removed.Add(uid);

                foreach ((uint itemId, uint count) in TableFields.Map(data, 22).Concat(TableFields.Map(data, 20)))
                    rewardService.Grant(accountUid, itemId, (int)count, messages);
            }

            if (removed.Count > 0)
                Log.Information("Account {Uid} dismantled weapons {Weapons}.", accountUid, string.Join(",", removed));

            return removed;
        }

        public bool? ToggleLock(uint accountUid, uint weaponUid)
        {
            WeaponEntity? weapon = Find(accountUid, weaponUid);
            if (weapon is null)
                return null;

            weapon.Flags ^= IWeaponProgressService.LockFlag;
            weaponService.Save();
            return (weapon.Flags & IWeaponProgressService.LockFlag) != 0;
        }

        public uint? Break(uint accountUid, uint weaponUid, uint materialUid)
        {
            AccountEntity account = accountService.GetByUid(accountUid)!;
            WeaponEntity? weapon = account.Weapons.FirstOrDefault(w => w.Id == weaponUid);
            WeaponEntity? material = account.Weapons.FirstOrDefault(w => w.Id == materialUid);
            if (weapon is null || material is null || weapon.Id == material.Id
                || material.WeaponId != weapon.WeaponId || material.GunId != 0
                || (material.Flags & IWeaponProgressService.LockFlag) != 0
                || (uint)weapon.BreakTimes >= IWeaponProgressService.MaxBreak)
            {
                LastError = weapon is null || material is null ? Common.Proto.HNINDAOJCKP.GunWeaponNotFound
                    : (uint)weapon.BreakTimes >= IWeaponProgressService.MaxBreak ? Common.Proto.HNINDAOJCKP.GunWeaponMaxBreak
                    : Common.Proto.HNINDAOJCKP.GunWeaponBreakMaterialInvalid;
                return null;
            }

            inventoryService.Remove(accountUid, material);
            weapon.BreakTimes++;
            weaponService.Save();
            Log.Information("Account {Uid} broke weapon {Weapon} to {Times} using duplicate {Material}.", accountUid, weaponUid, weapon.BreakTimes, materialUid);
            return (uint)weapon.BreakTimes;
        }

        private WeaponEntity? Find(uint accountUid, uint weaponUid) =>
            accountService.GetByUid(accountUid)?.Weapons.FirstOrDefault(w => w.Id == weaponUid);
    }
}
