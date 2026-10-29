using Microsoft.AspNetCore.Mvc;

namespace Griffin.SDKServer.Controllers
{
    /// <summary>Bootstrap responses used by the Android client's embedded Mica SDK.</summary>
    [ApiController]
    public sealed class MicaBootstrapController : ControllerBase
    {
        [HttpGet("/client_environment_config")]
        public IResult GetClientEnvironmentConfig(
            [FromQuery(Name = "game_channel_id")] string? gameChannelId,
            [FromQuery] string? version,
            [FromQuery(Name = "type_id")] string? typeId,
            [FromQuery(Name = "sub_id")] string? subId)
        {
            // The CN client expects this envelope and only consumes epic_env during startup.
            return Results.Json(new
            {
                code = 0,
                message = "OK",
                data = new { epic_env = "Live" },
            });
        }
    }
}
