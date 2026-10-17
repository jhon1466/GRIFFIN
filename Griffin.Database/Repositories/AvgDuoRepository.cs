using Griffin.Database.Core;
using Griffin.Database.Entities;

namespace Griffin.Database.Repositories
{
    public sealed class AvgDuoRepository(GriffinDbContext db) : Repository<AvgDuoEntity>(db), IAvgDuoRepository
    {
        public AvgDuoEntity? GetByUid(uint uid)
        {
            return Db.AvgDuos.SingleOrDefault(a => a.Account.Uid == uid);
        }
    }

    public interface IAvgDuoRepository : IRepository<AvgDuoEntity>
    {
        AvgDuoEntity? GetByUid(uint uid);
    }
}
