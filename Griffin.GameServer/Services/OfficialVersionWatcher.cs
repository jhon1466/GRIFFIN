using System.Text.Json;
using Griffin.Common.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Asks the official version service (<c>zone/client_res_v1</c>) which resources and tables the live game has and compares them with what this
    /// server announces (<see cref="StaticConfig"/>). It NEVER changes the announced versions by itself: a newer game can bring tables or messages the
    /// server does not understand yet, so the owner switches (GRIFFIN_AB_RESOURCE_VERSION / GRIFFIN_STC_VERSION) after testing. The result is only
    /// logged (once per change) and kept in <see cref="Latest"/> for the GM status (A-163). Disabled with GRIFFIN_VERSION_WATCH=0; network errors are ignored.
    /// </summary>
    public sealed class OfficialVersionWatcher(ILogger<OfficialVersionWatcher> logger) : Microsoft.Extensions.Hosting.BackgroundService
    {
        public sealed record Snapshot(string ResVersion, string StcVersion, string BinaryVersion, bool UpdateAvailable, DateTime CheckedAtUtc);

        public static Snapshot? Latest { get; private set; }

        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (Environment.GetEnvironmentVariable("GRIFFIN_VERSION_WATCH") == "0")
                return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string? reported = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string url = $"https://gf2-zoneinfo.sunborngame.com/zone/client_res_v1?type_id=3&version={StaticConfig.ClientVersion}&game_channel_id=5";
                    using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync(url, stoppingToken));
                    JsonElement data = document.RootElement.GetProperty("data");
                    string res = data.GetProperty("ResVersion").GetString() ?? string.Empty;
                    string stc = data.GetProperty("StcVersion").GetString() ?? string.Empty;
                    string binary = data.TryGetProperty("BinaryVersion", out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
                    bool newer = res != StaticConfig.AbResourceVersion || stc != StaticConfig.StcVersion;
                    Latest = new Snapshot(res, stc, binary, newer, DateTime.UtcNow);

                    string key = $"{res}|{stc}";
                    if (newer && key != reported)
                    {
                        logger.LogWarning(
                            "The official game has resources {Res} and tables {Stc}; this server announces {MyRes} and {MyStc}. Nothing was changed: after testing, set GRIFFIN_AB_RESOURCE_VERSION and GRIFFIN_STC_VERSION (or update StaticConfig).",
                            res, stc, StaticConfig.AbResourceVersion, StaticConfig.StcVersion);
                        reported = key;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // real shutdown
                    return;
                }
                catch (Exception ex)
                {
                    // ANY other failure, including HttpClient.Timeout (which throws TaskCanceledException, a subclass of
                    // OperationCanceledException): a version probe that cannot reach the official service must never stop the
                    // host. The earlier filter let the timeout escape and killed the whole server on the offline hotspot (A-173).
                    logger.LogDebug(ex, "The official version service could not be reached.");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
