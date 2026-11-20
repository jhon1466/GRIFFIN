using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.Database.Services;

namespace Griffin.GameServer.Services
{
    public sealed class WeaponModService(
        IWeaponModRepository weaponModRepository,
        IAccountService accountService,
        ITableService tableService) : IWeaponModService
    {
        public void AddAllWeaponMods(uint accountUid)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            List<WeaponModData> modData = tableService.GetTable<WeaponModData>();
            var existingModIds = new HashSet<uint>(account.WeaponMods.Select(m => m.WeaponModId));

            var newMods = modData
                .Where(d => d.Id != 0 && !existingModIds.Contains(d.Id))
                .Select(d =>
                {
                    ulong uid = GenerateUid();
                    return new WeaponModEntity
                    {
                        WeaponModId = d.Id,
                        Uid = uid,
                        Level = 0,
                        Field5 = 0,
                        Field6 = 0,
                        // the affixes of the mod (A-74): Field7 holds the four 16-bit pairs the client reads from GunWeaponMod field 3
                        Field7 = ModAffixes.Roll(tableService, d.Id, uid),
                    };
                })
                .ToList();

            account.AddWeaponMods(newMods);
            weaponModRepository.SaveChanges();
        }

        public WeaponModEntity[] GetPlayerWeaponMods(uint accountUid)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            // mods created before the affixes existed get them now (once)
            if (ModAffixes.Ensure(tableService, account.WeaponMods))
                weaponModRepository.SaveChanges();

            return account.WeaponMods.ToArray();
        }

        public void AddWeaponMod(uint accountUid, WeaponModEntity weaponMod)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            account.WeaponMods.Add(weaponMod);
            weaponModRepository.SaveChanges();
        }

        public bool RemoveWeaponMod(uint accountUid, WeaponModEntity weaponMod)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            if (!account.WeaponMods.Remove(weaponMod))
                return false;

            weaponModRepository.SaveChanges();
            return true;
        }

        /// <summary>A new mod (a drop, a reward) with its own affixes, saved so that its id exists.</summary>
        public WeaponModEntity Create(uint accountUid, uint weaponModStcId)
        {
            ulong uid = GenerateUid();
            // a wished effect (ModWish, weapon-part stages): the power of a mod is drawn from its uid, so take the next uid that draws that effect
            uint wished = ModWish.CurrentEffect;
            if (wished != 0)
            {
                WeaponModData? data = tableService.GetTable<WeaponModData>().FirstOrDefault(row => row.Id == weaponModStcId);
                uint plan = data is null ? 0 : TableFields.U(data, 12);
                IReadOnlyList<uint> powers = plan == 0 ? [] : tableService.GetTable<ModPowerBySuitPlanIdData>().Where(r => TableFields.U(r, 1) == plan)
                    .Select(r => TableFields.List(r, 2)).FirstOrDefault() ?? [];
                Dictionary<uint, uint> effects = tableService.GetTable<ModPowerData>().GroupBy(r => TableFields.U(r, 1)).ToDictionary(g => g.Key, g => TableFields.U(g.First(), 3));
                if (powers.Any(p => effects.GetValueOrDefault(p) == wished))
                    while (effects.GetValueOrDefault(powers[(int)(uid % (ulong)powers.Count)]) != wished)
                        uid = GenerateUid();
            }
            var mod = new WeaponModEntity
            {
                WeaponModId = weaponModStcId,
                Uid = uid,
                Level = 0,
                Field7 = ModAffixes.Roll(tableService, weaponModStcId, uid),
            };
            AddWeaponMod(accountUid, mod);
            return mod;
        }

        public void Save() => weaponModRepository.SaveChanges();

        // Mod uids are unique in the database across server restarts: seed the counter from the clock (milliseconds << 12) instead of 0.
        private static long nextUid = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 12;

        private static ulong GenerateUid() => (ulong)Interlocked.Increment(ref nextUid);
    }

    public interface IWeaponModService : IGameService
    {
        WeaponModEntity[] GetPlayerWeaponMods(uint accountUid);
        void AddWeaponMod(uint accountUid, WeaponModEntity weaponMod);
        bool RemoveWeaponMod(uint accountUid, WeaponModEntity weaponMod);
        void AddAllWeaponMods(uint accountUid);
        WeaponModEntity Create(uint accountUid, uint weaponModStcId);
        void Save();
    }
}
