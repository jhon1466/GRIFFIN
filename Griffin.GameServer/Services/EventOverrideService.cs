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

    public sealed class EventOverrideService : IEventOverrideService
    {
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
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Window window = new() { Open = now - 60, Close = now + (long)Math.Clamp(days, 1, 3650) * 86400 };
            lock (Gate)
            {
                Dictionary<uint, Window> all = Load();
                all[activityId] = window;
                Save(all);
            }

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
                return true;
            }
        }

        public (ulong Open, ulong Close) Effective(Google.Protobuf.IMessage row)
        {
            uint id = TableFields.U(row, 1);
            lock (Gate)
            {
                if (Load().TryGetValue(id, out Window? window))
                    return ((ulong)window.Open, (ulong)window.Close);
            }

            return (TableFields.U64(row, 4), TableFields.U64(row, 5));
        }
    }
}
