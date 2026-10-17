using Microsoft.AspNetCore.Mvc;
using Griffin.SDKServer.Models;

namespace Griffin.SDKServer.Controllers
{
    [ApiController]
    [Route("/sdk_log")]
    public class SdkLogController : ControllerBase
    {
        [HttpPost]
        public IResult PostSdkLog()
        {
            SdkLogDto response = new()
            {
                Code = 0,
                Msg = "OK",
                Data = string.Empty,
            };

            return Results.Json(response);
        }
    }
}
