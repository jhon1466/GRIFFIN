using Griffin.Database.Core;
using Griffin.Database.Entities;

namespace Griffin.Database.Repositories
{
    public sealed class WeaponModRepository(GriffinDbContext db) : Repository<WeaponModEntity>(db), IWeaponModRepository
    {
        public WeaponModEntity[] GetWeaponModsByUid(uint uid)
        {
            return Db.WeaponMods.Where(m => m.Account.Uid == uid).ToArray();
        }

        public WeaponModEntity? GetById(uint id) => Db.WeaponMods.SingleOrDefault(m => m.Id == id);
    }

    public interface IWeaponModRepository : IRepository<WeaponModEntity>
    {
        WeaponModEntity? GetById(uint id);
        WeaponModEntity[] GetWeaponModsByUid(uint uid);
    }
}
