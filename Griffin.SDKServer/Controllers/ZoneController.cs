using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Griffin.Common.Utils;
using Griffin.SDKServer.Models;
using Serilog;

namespace Griffin.SDKServer.Controllers
{
    [ApiController]
    [Route("/zone")]
    public class ZoneController : ControllerBase
    {
        [HttpGet("gatelist")]
        public IResult GetGatelist()
        {
            GatelistDto gatelist = new()
            {
                Code = 0,
                Msg = "OK",
                Data = [$"{ReachableGameHost()}:{StaticConfig.GameServerPort}"],
            };

            return Results.Json(gatelist);
        }

        /// <summary>
        /// The configured host is often a LAN address that comes and goes (e.g. the Windows hotspot
        /// 192.168.137.1). When it is a private address this PC no longer has, advertise the address
        /// the client used to reach us instead, so login does not hang before the lobby.
        /// Public addresses (VPS / relays) are always advertised as configured.
        /// </summary>
        private string ReachableGameHost()
        {
            string configured = StaticConfig.GameServerHost;
            if (!IPAddress.TryParse(configured, out IPAddress? ip) || !IsPrivate(ip) || IPAddress.IsLoopback(ip))
                return configured;
            bool local = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.Equals(ip));
            if (local)
                return configured;
            IPAddress? reached = HttpContext.Connection.LocalIpAddress;
            if (reached == null)
                return configured;
            if (reached.IsIPv4MappedToIPv6)
                reached = reached.MapToIPv4();
            if (reached.AddressFamily != AddressFamily.InterNetwork)
                return "127.0.0.1";
            Log.Warning("GameServer host {Configured} is not on this PC; advertising {Reached} instead", configured, reached);
            return reached.ToString();
        }

        private static bool IsPrivate(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        private static readonly HttpClient OfficialHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastOfficial = new(StringComparer.Ordinal);

        /// <summary>
        /// The game asks here which code, binary, resources and tables it must download. The answer is the official one for the
        /// exact platform and version the game sends, taken live, so every official update, pre-download or new build reaches the
        /// game the same way it does on the official server. Only the CDN address (when GRIFFIN_CDN_ORIGIN points elsewhere) and the
        /// shifted table set (when an event is moved) are replaced. Without the official service: its last answer, else the built-in profile.
        /// </summary>
        [HttpGet("client_res_v1")]
        public async Task<IResult> GetClientResV1(
            [FromQuery(Name = "type_id")] string? typeId,
            [FromQuery] string? version,
            [FromQuery(Name = "game_channel_id")] string? gameChannelId,
            [FromQuery] string? t)
        {
            OfficialResourceCatalog.NoteRequested(version);
            bool android = typeId == "1" || (typeId != "3" && StaticConfig.IsAndroidVersion(version));
            string key = $"{typeId}|{version}|{gameChannelId}";
            string? official = null;
            try
            {
                string url = "https://gf2-zoneinfo.sunborngame.com/zone/client_res_v1"
                    + $"?type_id={Uri.EscapeDataString(typeId ?? (android ? "1" : "3"))}"
                    + $"&version={Uri.EscapeDataString(version ?? (android ? StaticConfig.AndroidClientVersion : StaticConfig.ClientVersion))}"
                    + $"&game_channel_id={Uri.EscapeDataString(gameChannelId ?? "5")}";
                string body = await OfficialHttp.GetStringAsync(url, HttpContext.RequestAborted);
                using (System.Text.Json.JsonDocument check = System.Text.Json.JsonDocument.Parse(body))
                    if (check.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("ResVersion", out _))
                    {
                        official = body;
                        LastOfficial[key] = body;
                    }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
            {
                Log.Warning("Official client_res_v1 unreachable for {Key} ({Error}); using the last official answer", key, ex.Message);
            }
            official ??= LastOfficial.TryGetValue(key, out string? last) ? last : null;
            if (official is null)
                return Results.Json(CreateClientResV1(android, version));

            System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(official)!;
            System.Text.Json.Nodes.JsonNode data = root["data"]!;
            string? cdnOrigin = Environment.GetEnvironmentVariable("GRIFFIN_CDN_ORIGIN")?.TrimEnd('/');
            if (cdnOrigin is not null && !cdnOrigin.Contains("sunborngame.com", StringComparison.OrdinalIgnoreCase))
            {
                data["ResUrlCdn"] = $"{cdnOrigin}/game_resources";
                data["ResUrlOss"] = $"{cdnOrigin}/game_resources";
            }
            // the official forced-update thresholds reject the Griffin clients ("当前客户端版本已过时"): keep the server's own
            data["MustUpdateVersionClient"] = StaticConfig.MustUpdateVersionClient;
            data["MustUpdateVersionBin"] = StaticConfig.MustUpdateVersionBin;
            data["MustUpdateVersionStc"] = StaticConfig.MustUpdateVersionStc;
            if (ShiftedTables.Version is { } shifted)
                data["StcVersion"] = shifted;
            return Results.Text(root.ToJsonString(), "application/json");
        }

        private static ClientResV1Dto CreateClientResV1(bool android, string? requestedVersion = null)
        {
            string? cdnOrigin = Environment.GetEnvironmentVariable("GRIFFIN_CDN_ORIGIN")?.TrimEnd('/');
            string resUrl = StaticConfig.ResUrlCdn;
            if (cdnOrigin is not null && !cdnOrigin.Contains("sunborngame.com", StringComparison.OrdinalIgnoreCase))
                resUrl = $"{cdnOrigin}/game_resources";
            string clientVersion = android ? StaticConfig.AndroidClientVersion : StaticConfig.ClientVersion;
            // a client on a newer (or older) official build gets that build's official resources, not the configured build's
            if (!string.IsNullOrWhiteSpace(requestedVersion) && OfficialResourceCatalog.Knows(requestedVersion))
                clientVersion = requestedVersion;
            string configuredResVersion = android ? StaticConfig.AndroidAbResourceVersion : StaticConfig.AbResourceVersion;
            string resVersion = OfficialResourceCatalog.Resolve(clientVersion, configuredResVersion);
            return new ClientResV1Dto
            {
                Code = 0,
                Msg = "OK",
                Data = new ClientResV1Dto.ClientResV1DataDto
                {
                    AuditResVersion = resVersion,
                    ResVersion = resVersion,
                    ResUrlCdn = resUrl,
                    ResUrlOss = resUrl,
                    AppUpdateUrl = "https://gf2.sunborngame.com/",
                    StcVersion = ShiftedTables.Version ?? StaticConfig.StcVersion,
                    ModifyHotfix = false,
                    AppVersionCompatible = [string.Empty],
                    IsAudit = false,
                    BinaryVersion = OfficialResourceCatalog.ResolveBinary(
                        clientVersion, android ? StaticConfig.AndroidBinaryVersion : StaticConfig.BinaryVersion),
                    MustUpdateVersionClient = StaticConfig.MustUpdateVersionClient,
                    MustUpdateVersionBin = StaticConfig.MustUpdateVersionBin,
                    MustUpdateVersionStc = StaticConfig.MustUpdateVersionStc,
                    IsCloseBattle = 1,
                    PreDownloadResVersion = OfficialResourceCatalog.ResolvePreDownloadVersion(
                        clientVersion, android ? 29759 : 29758),
                },
            };
        }
    }
}
