using Microsoft.AspNetCore.Mvc;
using Griffin.SDKServer.Models;

namespace Griffin.SDKServer.Controllers
{
    [ApiController]
    [Route("/banner")]
    public class BannerController : ControllerBase
    {
        // Keep the locally hosted banner cards visible in clients using the current clock.
        // These static cards are retained for the private server; the previous Sep 2026
        // expiry made Android silently filter every banner even though /banner returned 200.
        private const string DefaultStartTime = "2026-01-01 00:00:00";
        private const string DefaultEndTime = "2037-12-31 23:59:59";
        private const long DefaultStartTimeTs = 1767225600;
        private const long DefaultEndTimeTs = 2145916799;

        [HttpGet]
        public IResult GetBanner(
            [FromQuery(Name = "game_channel_id")] string? gameChannelId,
            [FromQuery(Name = "type_id")] string? typeId,
            [FromQuery] string? t,
            [FromQuery] string? language,
            [FromQuery(Name = "sub_id")] string? subId)
        {
            // the lobby banners follow the official list (they change with every event); the old fixed list is only the fallback
            if (Official(Request.QueryString.Value ?? "") is { } official)
                return Results.Content(official, "application/json");
            int channel = int.TryParse(gameChannelId, out int parsedChannel) ? parsedChannel : 5;
            int type = int.TryParse(typeId, out int parsedType) ? parsedType : 1;
            return Results.Json(CreateBannerList(channel, type));
        }

        private const string OfficialBannerUrl = "https://gf2-zoneinfo.sunborngame.com/banner";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, string Body)> Cache = new();

        private static string? Official(string query)
        {
            if (Cache.TryGetValue(query, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
                return hit.Body;
            try
            {
                string body = Http.GetStringAsync(OfficialBannerUrl + query).GetAwaiter().GetResult();
                if (System.Text.Json.Nodes.JsonNode.Parse(body) is not System.Text.Json.Nodes.JsonObject root
                    || root["code"]?.GetValue<int>() != 0 || root["data"] is not System.Text.Json.Nodes.JsonArray items)
                    return hit.Body;
                // with a local CDN origin the pictures go through its /website proxy, like the patched client expects
                string? cdnOrigin = Environment.GetEnvironmentVariable("GRIFFIN_CDN_ORIGIN")?.TrimEnd('/');
                if (cdnOrigin is not null && !cdnOrigin.Contains("sunborngame.com", StringComparison.OrdinalIgnoreCase))
                    foreach (System.Text.Json.Nodes.JsonNode? item in items)
                        if (item?["pic_name"]?.GetValue<string>() is { } pic)
                            item["pic_url"] = $"{cdnOrigin}/website/platform/{pic}";
                body = root.ToJsonString();
                Cache[query] = (DateTime.UtcNow, body);
                return body;
            }
            catch (Exception)
            {
                return hit.Body;
            }
        }

        private static BannerDto CreateBannerList(int channelId, int typeId)
        {
            return new BannerDto
            {
                Code = 0,
                Msg = "OK",
                Data =
                [
                    Banner(
                        id: 543,
                        picName: "1786011907866.png",
                        sort: 544,
                        jumpId: 6372,
                        channelId: channelId,
                        typeId: typeId),
                    Banner(
                        id: 542,
                        picName: "1786011802695.png",
                        sort: 543,
                        jumpId: 5105,
                        channelId: channelId,
                        typeId: typeId),
                    Banner(
                        id: 541,
                        picName: "1786011736541.png",
                        sort: 542,
                        jumpId: 5116,
                        channelId: channelId,
                        typeId: typeId,
                        startTime: "2026-08-19 05:00:00",
                        startTimeTs: 1787086800),
                    Banner(
                        id: 540,
                        picName: "1786011644363.png",
                        sort: 541,
                        jumpId: 5117,
                        channelId: channelId,
                        typeId: typeId),
                    Banner(
                        id: 539,
                        picName: "1786011539480.png",
                        sort: 540,
                        jumpId: 30000,
                        channelId: channelId,
                        typeId: typeId),
                ],
            };
        }

        private static BannerDto.BannerItemDto Banner(
            int id,
            string picName,
            int sort,
            int jumpId,
            int channelId,
            int typeId,
            string? startTime = null,
            string? endTime = null,
            long? startTimeTs = null,
            long? endTimeTs = null)
        {
            string? cdnOrigin = Environment.GetEnvironmentVariable("GRIFFIN_CDN_ORIGIN")?.TrimEnd('/');
            string picUrl = cdnOrigin is not null
                && !cdnOrigin.Contains("sunborngame.com", StringComparison.OrdinalIgnoreCase)
                ? $"{cdnOrigin}/banner-assets/{picName}"
                : $"https://gf2-cn.cdn.sunborngame.com/website/platform/{picName}";

            return new BannerDto.BannerItemDto
            {
                Id = id,
                ChannelId = channelId,
                PicName = picName,
                PicUrl = picUrl,
                TypeId = typeId,
                StartTime = startTime ?? DefaultStartTime,
                EndTime = endTime ?? DefaultEndTime,
                StartTimeTs = startTimeTs ?? DefaultStartTimeTs,
                EndTimeTs = endTimeTs ?? DefaultEndTimeTs,
                Delay = 3,
                Sort = sort,
                JumpId = jumpId,
                SysId = 1000,
            };
        }
    }
}
