using Griffin.Database.Core;
using Griffin.Database.Entities;

namespace Griffin.Database.Repositories
{
    public sealed class WeaponModSkinRepository(GriffinDbContext db) : Repository<WeaponModSkinEntity>(db), IWeaponModSkinRepository
    {
        public WeaponModSkinEntity[] GetWeaponModSkinsByUid(uint uid)
        {
            return Db.WeaponModSkins.Where(s => s.Account.Uid == uid).ToArray();
        }

        public WeaponModSkinEntity? GetById(uint id) => Db.WeaponModSkins.SingleOrDefault(s => s.Id == id);
    }

    public interface IWeaponModSkinRepository : IRepository<WeaponModSkinEntity>
    {
        WeaponModSkinEntity? GetById(uint id);
        WeaponModSkinEntity[] GetWeaponModSkinsByUid(uint uid);
    }
}
