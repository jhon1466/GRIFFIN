using System.Collections.Concurrent;
using Google.Protobuf;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Services;

public interface IStaminaService : IGameService
{
    StaminaBalance GetOrSeed(uint accountUid, int startingValue, long refreshTime);

    bool TrySpend(uint accountUid, int amount, int startingValue, long refreshTime, out StaminaBalance balance);

    StaminaBalance Add(uint accountUid, int amount, int startingValue, long refreshTime);

    /// <summary>Adds stamina keeping the recovery clock as it is (for rewards, purchases and stamina items, which have no connection clock at hand).</summary>
    StaminaBalance AddKeepingClock(uint accountUid, int amount);
}

/// <summary>Account-persisted stamina from Resource.stamina_type_resource (item 101), which is separate from resource_ex.</summary>
public sealed class StaminaService(IAccountStateService accountState, Griffin.Common.Table.ITableService? tables = null) : IStaminaService
{
    private const string StateKey = "resource.stamina.101";
    public const int StartingValue = 183;

    /// <summary>
    /// Test/emergency switch only (GRIFFIN_STAMINA=full keeps the stamina at 240). The normal behaviour is the natural one: it refills with
    /// time (ItemData 101 column 18) or by purchase, as the user asked.
    /// </summary>
    private static bool KeepFull => string.Equals(Environment.GetEnvironmentVariable("GRIFFIN_STAMINA"), "full", StringComparison.OrdinalIgnoreCase);

    private const int FullValue = 240;
    private static readonly ConcurrentDictionary<uint, object> Locks = new();

    /// <summary>
    /// Natural recovery. ASSUMPTION (no official capture): <c>ItemData</c> row 101, column 18 = "300,1,240;" means a cap of 300, +1 point
    /// every 240 seconds. The balance stores the moment the recovery clock started (<c>RefreshTime</c>), as the client reads it; at the cap the
    /// clock restarts on the next spend. Without the table service (unit tests) nothing recovers.
    /// </summary>
    private (int Cap, int Interval)? Recovery()
    {
        if (tables is null)
            return null;

        Griffin.Common.Table.ItemData? row = tables.GetTable<Griffin.Common.Table.ItemData>().FirstOrDefault(r => r.Id == 101);
        string[] parts = (row is null ? string.Empty : GameServer.Services.TableFields.S(row, 18).TrimEnd(';')).Split(',');
        return parts.Length >= 3 && int.TryParse(parts[0], out int cap) && int.TryParse(parts[2], out int interval) && cap > 0 && interval > 0
            ? (cap, interval) : null;
    }

    private void Recover(StaminaBalance balance, long now)
    {
        if (Recovery() is not var (cap, interval) || now <= balance.RefreshTime)
            return;

        if (balance.Value >= cap)
        {
            balance.RefreshTime = now;
            return;
        }

        long gain = (now - balance.RefreshTime) / interval;
        if (gain <= 0)
            return;

        balance.Value = (int)Math.Min(cap, balance.Value + gain);
        balance.RefreshTime = balance.Value >= cap ? now : balance.RefreshTime + gain * interval;
    }

    public StaminaBalance GetOrSeed(uint accountUid, int startingValue, long refreshTime)
    {
        lock (Locks.GetOrAdd(accountUid, static _ => new object()))
        {
            StaminaBalance? balance = accountState.Get<StaminaBalance>(accountUid, StateKey);
            if (balance is not null)
            {
                Recover(balance, refreshTime);
                if (KeepFull && balance.Value < FullValue)
                    balance.Value = FullValue;
                accountState.Set(accountUid, StateKey, balance);
                return balance;
            }

            balance = new StaminaBalance { Value = Math.Max(0, startingValue), RefreshTime = Math.Max(0, refreshTime) };
            accountState.Set(accountUid, StateKey, balance);
            return balance;
        }
    }

    public bool TrySpend(uint accountUid, int amount, int startingValue, long refreshTime, out StaminaBalance balance)
    {
        if (amount <= 0)
        {
            balance = GetOrSeed(accountUid, startingValue, refreshTime);
            return amount == 0;
        }

        lock (Locks.GetOrAdd(accountUid, static _ => new object()))
        {
            balance = accountState.Get<StaminaBalance>(accountUid, StateKey)
                ?? new StaminaBalance { Value = Math.Max(0, startingValue), RefreshTime = Math.Max(0, refreshTime) };
            Recover(balance, refreshTime);
            if (balance.Value < amount)
            {
                accountState.Set(accountUid, StateKey, balance);
                return false;
            }

            bool wasFull = Recovery() is { } r && balance.Value >= r.Cap;
            balance.Value -= amount;
            if (KeepFull && balance.Value < FullValue)
                balance.Value = FullValue;
            if (Recovery() is null || wasFull)
                balance.RefreshTime = Math.Max(0, refreshTime);
            accountState.Set(accountUid, StateKey, balance);
            return true;
        }
    }

    public StaminaBalance AddKeepingClock(uint accountUid, int amount)
    {
        lock (Locks.GetOrAdd(accountUid, static _ => new object()))
        {
            StaminaBalance balance = accountState.Get<StaminaBalance>(accountUid, StateKey)
                ?? new StaminaBalance { Value = StartingValue, RefreshTime = Griffin.Common.Networking.Connection.GameClockBase };
            balance.Value = (int)Math.Min(int.MaxValue, (long)balance.Value + Math.Max(0, amount));
            accountState.Set(accountUid, StateKey, balance);
            return balance;
        }
    }

    public StaminaBalance Add(uint accountUid, int amount, int startingValue, long refreshTime)
    {
        if (amount <= 0)
            return GetOrSeed(accountUid, startingValue, refreshTime);

        lock (Locks.GetOrAdd(accountUid, static _ => new object()))
        {
            StaminaBalance balance = accountState.Get<StaminaBalance>(accountUid, StateKey)
                ?? new StaminaBalance { Value = Math.Max(0, startingValue), RefreshTime = Math.Max(0, refreshTime) };
            Recover(balance, refreshTime);
            balance.Value = (int)Math.Min(int.MaxValue, (long)balance.Value + amount);
            if (Recovery() is null)
                balance.RefreshTime = Math.Max(0, refreshTime);
            accountState.Set(accountUid, StateKey, balance);
            return balance;
        }
    }

    public static byte[] BuildUpdatePayload(uint itemId, StaminaBalance balance)
    {
        byte[] stamina = new StaminaTypeResource
        {
            RefreshTime = balance.RefreshTime,
            Value = balance.Value,
        }.ToByteArray();

        using var payload = new MemoryStream();
        using (var coded = new CodedOutputStream(payload, leaveOpen: true))
        {
            coded.WriteTag(1, WireFormat.WireType.Varint);
            coded.WriteUInt32(itemId);
            coded.WriteTag(2, WireFormat.WireType.LengthDelimited);
            coded.WriteBytes(ByteString.CopyFrom(stamina));
            coded.Flush();
        }
        return payload.ToArray();
    }
}

public sealed class StaminaBalance
{
    public int Value { get; set; }
    public long RefreshTime { get; set; }
}
