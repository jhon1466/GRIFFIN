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
                Data = [StaticConfig.GameServerAddress],
            };

            return Results.Json(gatelist);
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
            string resVersion = android ? StaticConfig.AndroidAbResourceVersion : StaticConfig.AbResourceVersion;
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
                    StcVersion = StaticConfig.StcVersion,
                    ModifyHotfix = false,
                    AppVersionCompatible = [string.Empty],
                    IsAudit = false,
                    BinaryVersion = android ? StaticConfig.AndroidBinaryVersion : StaticConfig.BinaryVersion,
                    MustUpdateVersionClient = StaticConfig.MustUpdateVersionClient,
                    MustUpdateVersionBin = StaticConfig.MustUpdateVersionBin,
                    MustUpdateVersionStc = StaticConfig.MustUpdateVersionStc,
                    IsCloseBattle = 1,
                    PreDownloadResVersion = 28495,
                },
            };
        }
    }
}
