using Microsoft.AspNetCore.Mvc;
using Griffin.SDKServer.Models;
using Griffin.SDKServer.Services;

namespace Griffin.SDKServer.Controllers.Auth
{
    [ApiController]
    [Route("/email")]
    public class EmailController(IAuthService authService) : ControllerBase
    {
        [HttpPost("login")]
        public IResult PostLogin([FromBody] EmailLoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Account) || string.IsNullOrWhiteSpace(request.Password))
            {
                return LoginFailureDto.Result(10001, "Invalid parameters");
            }

            var result = authService.EmailLogin(request.Account, request.Password);

            return Results.Json(result);
        }
    }
}
