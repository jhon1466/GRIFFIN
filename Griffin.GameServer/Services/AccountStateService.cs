using System.Text.Json;
using Griffin.Database.Repositories;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Typed JSON values per account on top of the <c>AccountStates</c> table (pity, purchase history, mail, claimed
    /// rewards...). One key holds one JSON document; <see cref="Set{T}"/> replaces it.
    /// </summary>
    public interface IAccountStateService : IGameService
    {
        T? Get<T>(uint accountUid, string key);

        void Set<T>(uint accountUid, string key, T value);

        bool Delete(uint accountUid, string key);
    }

    public sealed class AccountStateService(IAccountStateRepository repository) : IAccountStateService
    {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = false, WriteIndented = false };

        public T? Get<T>(uint accountUid, string key)
        {
            string? json = repository.GetByKey(accountUid, key)?.Json;
            if (string.IsNullOrWhiteSpace(json))
                return default;

            try
            {
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (JsonException ex)
            {
                Serilog.Log.Warning(ex, "Account state {Key} of account {Uid} is not valid {Type} JSON; ignoring it.", key, accountUid, typeof(T).Name);
                return default;
            }
        }

        public void Set<T>(uint accountUid, string key, T value) =>
            repository.Set(accountUid, key, JsonSerializer.Serialize(value, Options));

        public bool Delete(uint accountUid, string key) => repository.Delete(accountUid, key);
    }
}
