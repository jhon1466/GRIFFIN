using System.Collections.Concurrent;

namespace Griffin.GameServer.Handlers;

/// <summary>Serializes all difficult-mode state transitions for one account across its handlers.</summary>
internal static class DifficultAccountLocks
{
    private static readonly ConcurrentDictionary<uint, object> Locks = new();

    public static object For(uint accountUid) => Locks.GetOrAdd(accountUid, static _ => new object());
}
