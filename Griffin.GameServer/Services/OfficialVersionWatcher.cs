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
    public sealed class OfficialVersionWatcher(ILogger<OfficialVersionWatcher> logger, Griffin.Common.Table.ITableService tables) : Microsoft.Extensions.Hosting.BackgroundService
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
            Dictionary<string, string> latestByPlatform = new(StringComparer.Ordinal);
            while (!stoppingToken.IsCancellationRequested)
            {
                // the official service answers with the resources of the build it is asked about, so asking only about the configured
                // builds never shows a newer client: ask with a build that does not exist (it answers with the latest one) as well
                foreach (string typeId in new[] { "3", "1" })
                {
                    try
                    {
                        JsonElement data = await QueryAsync(http, typeId, "4.0.99999.0.0", stoppingToken);
                        string latestBuild = OfficialResourceCatalog.BuildOfVersion(data.GetProperty("ResVersion").GetString());
                        if (!string.IsNullOrEmpty(latestBuild))
                        {
                            if (!latestByPlatform.TryGetValue(typeId, out string? seen) || seen != latestBuild)
                            {
                                string configured = OfficialResourceCatalog.BuildOfVersion(typeId == "1" ? StaticConfig.AndroidClientVersion : StaticConfig.ClientVersion);
                                if (latestBuild != configured)
                                    logger.LogWarning("The official {Platform} client is now build {Latest} (configured {Configured}); following its CDN resources.",
                                        typeId == "1" ? "Android" : "PC", latestBuild, configured);
                            }
                            latestByPlatform[typeId] = latestBuild;
                            OfficialResourceCatalog.NoteRequested(latestBuild + ".0.0");
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogDebug(ex, "The official version service could not be reached for the latest build."); }
                }

                IEnumerable<string> clientVersions = new[] { StaticConfig.ClientVersion, StaticConfig.AndroidClientVersion }
                    .Concat(OfficialResourceCatalog.KnownBuilds().Select(b => b + ".0.0"));
                Dictionary<string, string> byBuild = new(StringComparer.Ordinal);
                foreach (string v in clientVersions)
                    byBuild.TryAdd(OfficialResourceCatalog.BuildOfVersion(v), v);

                foreach (string clientVersion in byBuild.Values)
                {
                    bool configuredAndroid = StaticConfig.IsAndroidVersion(clientVersion);
                    string[] typeIds = clientVersion == StaticConfig.ClientVersion ? ["3"] : configuredAndroid ? ["1"] : ["3", "1"];
                    foreach (string typeId in typeIds)
                    {
                        try
                        {
                            JsonElement data = await QueryAsync(http, typeId, clientVersion, stoppingToken);
                            string res = data.GetProperty("ResVersion").GetString() ?? string.Empty;
                            string stc = data.GetProperty("StcVersion").GetString() ?? string.Empty;
                            string binary = data.TryGetProperty("BinaryVersion", out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
                            int preDownload = data.TryGetProperty("PreDownloadResVersion", out JsonElement p) && p.TryGetInt32(out int pre) ? pre : 0;

                            // a build unknown to this platform answers with another build's resources: not this platform's build
                            if (!OfficialResourceCatalog.TryPublish(clientVersion, res, preDownload))
                                continue;
                            OfficialResourceCatalog.PublishBinary(clientVersion, binary);

                            string key = $"{clientVersion}|{res}";
                            if (reported.TryAdd(key, key))
                                logger.LogInformation("Official CDN resources {Res} (binary {Binary}) are now announced for client build {ClientVersion}.", res, binary, clientVersion);

                            bool isLatest = latestByPlatform.TryGetValue(typeId, out string? latest)
                                && latest == OfficialResourceCatalog.BuildOfVersion(clientVersion);
                            if (isLatest || (!latestByPlatform.ContainsKey(typeId)
                                && (clientVersion == StaticConfig.ClientVersion || configuredAndroid)))
                            {
                                bool newer = res != StaticConfig.AbResourceVersion || stc != StaticConfig.StcVersion;
                                Latest = new Snapshot(res, stc, binary, newer, DateTime.UtcNow);
                            }

                            // the tables follow the official ones while the server runs too (both platforms share them), so the client
                            // never has to download an older set when it comes from the official server
                            if (!string.IsNullOrWhiteSpace(stc) && !StaticConfig.StcVersionPinnedByEnv && stc != StaticConfig.StcVersion
                                && tables.UpdateTo(stc))
                                EventOverrideService.Apply(tables);
                            break;
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
                }

                // wake early when a client shows up with a build the catalog does not know yet
                try { await OfficialResourceCatalog.BuildRequested.WaitAsync(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static async Task<JsonElement> QueryAsync(HttpClient http, string typeId, string clientVersion, CancellationToken token)
        {
            string url = $"https://gf2-zoneinfo.sunborngame.com/zone/client_res_v1?type_id={typeId}&version={Uri.EscapeDataString(clientVersion)}&game_channel_id=5";
            using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync(url, token));
            return document.RootElement.GetProperty("data").Clone();
        }
    }
}
