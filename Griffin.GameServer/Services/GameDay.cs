namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The one "game day" of the server. The game resets daily content at 05:00 China time (21:00 UTC; the lobby kitchen already used it), so
    /// the day number is the UTC time shifted by 3 hours. Quests, check-ins, raids and events used a China midnight day (UTC+8) until now,
    /// which disagreed with the kitchen by five hours. ASSUMPTION: 05:00 CST is the reset hour for every daily system.
    /// </summary>
    public static class GameDay
    {
        public static readonly TimeSpan ResetShift = TimeSpan.FromHours(3);

        public static DateTimeOffset Now() => DateTimeOffset.UtcNow.ToOffset(ResetShift);

        /// <summary>yyyy-MM-dd of the current game day.</summary>
        public static string Key() => Now().ToString("yyyy-MM-dd");

        /// <summary>Number of the game day of a unix time (seconds).</summary>
        public static long Number(long unixSeconds) => (unixSeconds + (long)ResetShift.TotalSeconds) / 86400;
    }
}
