using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.Database.Services;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    public sealed class WeaponService(
        IWeaponRepository weaponRepository,
        IAccountService accountService,
        ITableService tableService) : IWeaponService
    {
        public void AddAllWeapons(uint accountUid)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            List<GunWeaponData> weaponData = tableService.GetTable<GunWeaponData>();
            var existingWeaponIds = new HashSet<uint>(account.Weapons.Select(w => w.WeaponId));

            var newWeapons = weaponData
                .Where(d => d.Id != 0 && !existingWeaponIds.Contains(d.Id))
                .Select(d => new WeaponEntity
                {
                    WeaponId = d.Id,
                    Level = 1,
                    CurExp = 0,
                    BreakTimes = 1,
                    GunId = 0,
                    Flags = 0,
                    EquippedSkinId = 0,
                    EquippedModIds = [],
                    TimeCreated = DateTime.UtcNow,
                })
                .ToList();

            account.AddWeapons(newWeapons);
            weaponRepository.SaveChanges();
        }

        public void Save() => weaponRepository.SaveChanges();

        public WeaponEntity[] GetPlayerWeapons(uint accountUid)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            return account.Weapons.ToArray();
        }

        public void AddWeapon(uint accountUid, WeaponEntity weapon)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            account.Weapons.Add(weapon);
            weaponRepository.SaveChanges();
        }

        public bool RemoveWeapon(uint accountUid, WeaponEntity weapon)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            if (!account.Weapons.Remove(weapon))
                return false;

            weaponRepository.SaveChanges();
            return true;
        }
    }

    public interface IWeaponService : IGameService
    {
        WeaponEntity[] GetPlayerWeapons(uint accountUid);

        /// <summary>Persists changes made to the tracked <see cref="WeaponEntity"/> instances.</summary>
        void Save();
        void AddWeapon(uint accountUid, WeaponEntity weapon);
        bool RemoveWeapon(uint accountUid, WeaponEntity weapon);
        void AddAllWeapons(uint accountUid);
    }
}
