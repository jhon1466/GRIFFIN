using Griffin.Database.Core;
using Griffin.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Griffin.Database.Repositories;

public sealed class AccountStateRepository(GriffinDbContext db)
    : Repository<AccountStateEntity>(db), IAccountStateRepository
{
    public AccountStateEntity? GetByKey(uint accountUid, string key) =>
        Db.AccountStates.SingleOrDefault(state => state.AccountUid == accountUid && state.Key == key);

    public void Set(uint accountUid, string key, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(json);

        DateTime updatedAt = DateTime.UtcNow;
        Db.SaveChanges();

        // A read-then-insert upsert races when two handlers persist the same account key.
        // Let PostgreSQL's unique index serialize the insert/update as a single statement.
        Db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO "AccountStates" ("AccountUid", "Key", "Json", "UpdatedAt")
            VALUES ({accountUid}, {key}, CAST({json} AS jsonb), {updatedAt})
            ON CONFLICT ("AccountUid", "Key")
            DO UPDATE SET "Json" = EXCLUDED."Json", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """);

        // Raw SQL bypasses EF's change tracker; keep any already-loaded instance current.
        AccountStateEntity? tracked = Db.AccountStates.Local
            .SingleOrDefault(state => state.AccountUid == accountUid && state.Key == key);
        if (tracked is not null)
        {
            tracked.Json = json;
            tracked.UpdatedAt = updatedAt;
        }
    }

    public bool Delete(uint accountUid, string key)
    {
        var state = GetByKey(accountUid, key);
        if (state is null)
            return false;

        Db.AccountStates.Remove(state);
        Db.SaveChanges();
        return true;
    }
}

public interface IAccountStateRepository : IRepository<AccountStateEntity>
{
    AccountStateEntity? GetByKey(uint accountUid, string key);
    void Set(uint accountUid, string key, string json);
    bool Delete(uint accountUid, string key);
}
