using System.Collections.Concurrent;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// One lock object per account for the operations that read a state, pay something and write the state back (claims, unlocks, upgrades): two
    /// connections of the same account (or a repeated click) could otherwise both read "not claimed yet" and pay twice.
    /// </summary>
    public static class AccountLock
    {
        private static readonly ConcurrentDictionary<uint, object> Locks = new();

        public static object For(uint uid) => Locks.GetOrAdd(uid, static _ => new object());
    }
}
