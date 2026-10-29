using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Griffin.SDKServer.Controllers
{
    [ApiController]
    [Route("/")]
    public class SDKController : ControllerBase
    {
        // The Android client geolocates itself with an ip-api.com style query (GET /json/?fields=...) and fails with
        // "解析错误提示" (parse error) when the body is empty (A-174).
        [HttpGet("json")]
        public IResult IpGeo() => Results.Json(new
        {
            query = HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "127.0.0.1",
            status = "success",
            continent = "Asia",
            country = "China",
            countryCode = "CN",
            region = "SH",
            regionName = "Shanghai",
            city = "Shanghai",
            zip = "",
            lat = 31.2222,
            lon = 121.4581,
            timezone = "Asia/Shanghai",
            offset = 28800,
            currency = "CNY",
            isp = "",
            org = "",
            @as = "",
            mobile = false,
            proxy = false,
            hosting = false,
        });

        [HttpGet("{*catchAll}")]
        public IResult CatchAllGet(string catchAll)
        {
            Log.Information($"HttpGet: {catchAll}");
            return Results.Empty;
        }

        [HttpPost("{*catchAll}")]
        public IResult CatchAllPost(string catchAll)
        {
            Log.Information($"HttpPost: {catchAll}");
            return Results.Empty;
        }
    }
}

