using System.Text.Json;
using Griffin.Common.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Polls the official version service. It hot-publishes resource bundles only when they belong to the exact same
    /// client build, allowing connected clients to download official CDN data without restarting Griffin. Table and
    /// binary versions are never changed automatically. Explicit GRIFFIN_AB_RESOURCE_VERSION pins take precedence.
    /// </summary>
    public sealed class OfficialVersionWatcher(ILogger<OfficialVersionWatcher> logger) : Microsoft.Extensions.Hosting.BackgroundService
    {
        public sealed record Snapshot(string ResVersion, string StcVersion, string BinaryVersion, bool UpdateAvailable, DateTime CheckedAtUtc);

        public static Snapshot? Latest { get; private set; }

        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (Environment.GetEnvironmentVariable("GRIFFIN_VERSION_WATCH") == "0")
                return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            Dictionary<string, string> reported = new(StringComparer.Ordinal);
            string[] clientVersions = [StaticConfig.ClientVersion, StaticConfig.AndroidClientVersion];
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (string clientVersion in clientVersions.Distinct(StringComparer.Ordinal))
                {
                    try
                    {
                        string typeId = StaticConfig.IsAndroidVersion(clientVersion) ? "1" : "3";
                        string url = $"https://gf2-zoneinfo.sunborngame.com/zone/client_res_v1?type_id={typeId}&version={Uri.EscapeDataString(clientVersion)}&game_channel_id=5";
                        using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync(url, stoppingToken));
                        JsonElement data = document.RootElement.GetProperty("data");
                        string res = data.GetProperty("ResVersion").GetString() ?? string.Empty;
                        string stc = data.GetProperty("StcVersion").GetString() ?? string.Empty;
                        string binary = data.TryGetProperty("BinaryVersion", out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
                        int preDownload = data.TryGetProperty("PreDownloadResVersion", out JsonElement p) && p.TryGetInt32(out int pre) ? pre : 0;

                        bool sameBuild = OfficialResourceCatalog.TryPublish(clientVersion, res, preDownload);
                        if (!sameBuild)
                        {
                            string mismatchKey = $"{clientVersion}|{res}";
                            if (!reported.ContainsKey(mismatchKey))
                                logger.LogWarning("Ignoring official resources {Res} for configured client {ClientVersion}: the client build differs.", res, clientVersion);
                            reported[mismatchKey] = mismatchKey;
                        }
                        else
                        {
                            string key = $"{clientVersion}|{res}";
                            if (!reported.ContainsKey(key))
                                logger.LogInformation("Official CDN resources {Res} are now announced for client build {ClientVersion}; table version remains pinned at {Stc}.", res, clientVersion, StaticConfig.StcVersion);
                            reported[key] = key;
                        }

                        if (string.Equals(clientVersion, StaticConfig.ClientVersion, StringComparison.Ordinal)
                            || StaticConfig.IsAndroidVersion(clientVersion))
                        {
                            bool newer = res != StaticConfig.AbResourceVersion || stc != StaticConfig.StcVersion;
                            Latest = new Snapshot(res, stc, binary, newer, DateTime.UtcNow);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "The official version service could not be reached for client {ClientVersion}.", clientVersion);
                    }
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
