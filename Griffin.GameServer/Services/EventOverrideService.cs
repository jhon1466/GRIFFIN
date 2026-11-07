using System.Text.Json;
using Griffin.Common.Table;
using Griffin.Common.Utils;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Events (ActivityListData, 589 rows: column 2 = type, 3 = name, 4/5 = official open/close) opened by hand from the GM tools or the
    /// <c>/evento</c> command, so a past event can be played again. An opened event gets a new window (from now for the given days) that
    /// replaces the official dates everywhere the server reports the event to the client. The list is shared by every account and kept in
    /// <c>Resources/event-overrides.json</c>, so it survives a restart.
    /// </summary>
    public interface IEventOverrideService : IGameService
    {
        IReadOnlyDictionary<uint, EventOverrideService.Window> All();

        EventOverrideService.Window Open(uint activityId, int days);

        bool Close(uint activityId);

        /// <summary>Open/close of the row: the hand-made window when there is one, otherwise columns 4 and 5.</summary>
        (ulong Open, ulong Close) Effective(Google.Protobuf.IMessage row);
    }

    public sealed class EventOverrideService(ITableService tableService) : IEventOverrideService
    {
        /// <summary>
        /// Rebuilds the shifted tables (ShiftedTables) for the opened events and makes the server read them. A closed event, or one
        /// whose window has passed, is dropped. Called at start and after every change.
        /// </summary>
        public static void Apply(ITableService tables)
        {
            List<ShiftedTables.Shift> shifts;
            lock (Gate)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Dictionary<uint, Window> all = Load();
                foreach (uint expired in all.Where(p => p.Value.Close < now).Select(p => p.Key).ToList())
                    all.Remove(expired);
                shifts = all.Select(p => new ShiftedTables.Shift(p.Key, p.Value.Open, p.Value.Close)).ToList();
            }

            ShiftedTables.Rebuild(Path.Combine(StaticConfig.ResourceDir, "Tables"), StaticConfig.StcVersion, shifts);
            tables.ClearCache();
        }

        public sealed class Window
        {
            public long Open { get; set; }

            public long Close { get; set; }
        }

        private static readonly object Gate = new();
        private static Dictionary<uint, Window>? cache;

        private static string FilePath => Path.Combine(StaticConfig.ResourceDir, "event-overrides.json");

        private static Dictionary<uint, Window> Load()
        {
            if (cache is not null)
                return cache;
            try
            {
                cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<uint, Window>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch (Exception)
            {
                cache = [];
            }

            return cache;
        }

        private static void Save(Dictionary<uint, Window> all)
        {
            cache = all;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }

        public IReadOnlyDictionary<uint, Window> All()
        {
            lock (Gate)
                return new Dictionary<uint, Window>(Load());
        }

        public Window Open(uint activityId, int days)
        {
            // the event keeps at least its official length; "days" can make it longer
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            (long open, long close) = ShiftedTables.OfficialActivityDates(Path.Combine(StaticConfig.ResourceDir, "Tables")).GetValueOrDefault(activityId);
            long length = close > open ? close - open : 0;
            Window window = new() { Open = now - 60, Close = now - 60 + Math.Max(length, (long)Math.Clamp(days, 1, 3650) * 86400) };
            lock (Gate)
            {
                Dictionary<uint, Window> all = Load();
                all[activityId] = window;
                Save(all);
            }

            Apply(tableService);
            return window;
        }

        public bool Close(uint activityId)
        {
            lock (Gate)
            {
                Dictionary<uint, Window> all = Load();
                if (!all.Remove(activityId))
                    return false;
                Save(all);
            }

            Apply(tableService);
            return true;
        }

        public (ulong Open, ulong Close) Effective(Google.Protobuf.IMessage row)
        {
            // the shifted tables already carry the new dates of a reopened event
            return (TableFields.U64(row, 4), TableFields.U64(row, 5));
        }
    }
}
