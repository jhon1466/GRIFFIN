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

        [HttpGet("client_res_v1")]
        public IResult GetClientResV1(
            [FromQuery(Name = "type_id")] string? typeId,
            [FromQuery] string? version,
            [FromQuery(Name = "game_channel_id")] string? gameChannelId,
            [FromQuery] string? t)
        {
            ClientResV1Dto clientRes = CreateClientResV1(StaticConfig.IsAndroidVersion(version));

            return Results.Json(clientRes);
        }

        private static ClientResV1Dto CreateClientResV1(bool android)
        {
            string? cdnOrigin = Environment.GetEnvironmentVariable("GRIFFIN_CDN_ORIGIN")?.TrimEnd('/');
            string resUrl = StaticConfig.ResUrlCdn;
            if (cdnOrigin is not null && !cdnOrigin.Contains("sunborngame.com", StringComparison.OrdinalIgnoreCase))
                resUrl = $"{cdnOrigin}/game_resources";
            string clientVersion = android ? StaticConfig.AndroidClientVersion : StaticConfig.ClientVersion;
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
                    BinaryVersion = android ? StaticConfig.AndroidBinaryVersion : StaticConfig.BinaryVersion,
                    MustUpdateVersionClient = StaticConfig.MustUpdateVersionClient,
                    MustUpdateVersionBin = StaticConfig.MustUpdateVersionBin,
                    MustUpdateVersionStc = StaticConfig.MustUpdateVersionStc,
                    IsCloseBattle = 1,
                    PreDownloadResVersion = OfficialResourceCatalog.ResolvePreDownloadVersion(
                        clientVersion, android ? 29122 : 29123),
                },
            };
        }
    }
}
