using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The remoulding core of a character (weapon panel, "改造核心"). The client keeps the core of each character in
    /// <c>Gun.grass_group</c> (field 55: core id, plugin uids, core level) and shows the toast "人形达到55级后解锁" when the list is empty.
    /// ASSUMPTION A-54: a character owns its core (the CoreData row named after it, see <c>tools/gen-cores.py</c>) from level
    /// <see cref="UnlockLevel"/> on; the server sends it in the gun list and, when a level-up crosses the level, in
    /// <c>SC_GunLevelUp</c> (field 4 lists the new core ids). The core level is saved in the gun's <c>GunTalentConsume</c> map under the
    /// reserved key <see cref="StorageKey"/> (no database column needed; never sent to the client) and raising it is free.
    /// </summary>
    public static partial class RemouldingCores
    {
        public const uint UnlockLevel = 55;

        public const uint MaxLevel = 60;

        /// <summary>Key of <c>GunEntity.GunTalentConsume</c> that stores the core level (item ids never get close to it).</summary>
        public const uint StorageKey = 0xFFFF0001;

        private static Dictionary<uint, uint>? coreOfGun;

        /// <summary>The core (CoreData id) of the character; 0 when it has none.</summary>
        public static uint CoreOf(uint gunId)
        {
            coreOfGun ??= Pairs.ToDictionary(p => p.Gun, p => p.Core);
            return coreOfGun.GetValueOrDefault(gunId);
        }

        /// <summary>The core the character owns now (level reached), 0 when it has not unlocked it.</summary>
        public static uint OwnedCore(GunEntity gun) => gun.Level >= UnlockLevel ? CoreOf(gun.GunId) : 0;

        public static uint LevelOf(GunEntity gun) => Math.Clamp(gun.GunTalentConsume.GetValueOrDefault(StorageKey, 1u), 1u, MaxLevel);

        /// <summary>(Data Links, gold) to take a core from one level to another: <c>LevelUpData</c> column 2 and 6 hold the totals accumulated up to each level.</summary>
        public static (uint Links, uint Gold) CostBetween(Griffin.Common.Table.ITableService tables, uint from, uint to)
        {
            uint Total(uint level, int column) =>
                tables.GetTable<LevelUpData>().Where(row => TableFields.U(row, 1) == level).Select(row => TableFields.U(row, column)).FirstOrDefault();

            return (Total(to, 2) - Math.Min(Total(from, 2), Total(to, 2)), Total(to, 6) - Math.Min(Total(from, 6), Total(to, 6)));
        }

        public static void SetLevel(GunEntity gun, uint level) => gun.GunTalentConsume[StorageKey] = Math.Clamp(level, 1u, MaxLevel);
    }
}
