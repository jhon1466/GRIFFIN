using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;

namespace Griffin.SDKServer
{
    /// <summary>
    /// The game's libcurl (UnityWebRequest) received complete SDK responses from the server but never considered the
    /// transfer finished ("Curl error 28: Operation timed out after 15000 milliseconds with N bytes received",
    /// seen in the client's Player.log on 2026-10-14). This middleware makes every SDK response self-delimiting and
    /// closes the connection after it: buffered body with an explicit Content-Length plus "Connection: close".
    /// It also logs the protocol details that matter for diagnosing client/server HTTP mismatches.
    /// </summary>
    public sealed class SdkHttpMiddleware(RequestDelegate next, ILogger<SdkHttpMiddleware> logger, CdnProxy cdnProxy)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            HttpRequest request = context.Request;

            if (CdnProxy.Matches(request.Path))
            {
                await cdnProxy.ForwardAsync(context);
                return;
            }

            string tls = context.Features.Get<ITlsHandshakeFeature>()?.Protocol.ToString() ?? "none";

            logger.LogInformation(
                "SDK request {Method} {Path} proto={Protocol} tls={Tls} len={Length} expect={Expect} conn={Connection} ua={Agent}",
                request.Method,
                request.Path + request.QueryString,
                request.Protocol,
                tls,
                request.ContentLength?.ToString() ?? "-",
                request.Headers.Expect.ToString(),
                request.Headers.Connection.ToString(),
                request.Headers.UserAgent.ToString());

            Stream originalBody = context.Response.Body;
            await using MemoryStream buffer = new();
            context.Response.Body = buffer;

            try
            {
                await next(context);

                if (!context.Response.HasStarted)
                {
                    context.Response.Headers.Remove("Transfer-Encoding");
                    context.Response.ContentLength = buffer.Length;
                    context.Response.Headers.Connection = "close";
                }

                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody, context.RequestAborted);
                logger.LogInformation(
                    "SDK response {Path} status={Status} bytes={Bytes} type={Type}",
                    request.Path, context.Response.StatusCode, buffer.Length, context.Response.ContentType ?? "-");
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }
    }

    internal sealed class SdkHttpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseMiddleware<SdkHttpMiddleware>();
            next(app);
        };
    }
}
