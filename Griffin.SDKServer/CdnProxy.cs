using System.Net.Http.Headers;
using Griffin.Common.Utils;

namespace Griffin.SDKServer
{
    /// <summary>
    /// Griffin Patcher rewrites every *.sunborngame.com URL to https://127.0.0.1 keeping only the path, including the
    /// resource CDN (gf2-cn.cdn.sunborngame.com). Without this proxy the client's downloads of game data (binary data,
    /// tables, asset bundles) hit this server, get a non-archive answer and fail with
    /// "unknown error during decompression". Static resource paths are therefore forwarded unchanged to the official CDN.
    /// </summary>
    public sealed class CdnProxy(ILogger<CdnProxy> logger)
    {
        private static readonly string[] ProxiedPrefixes = ["/game_resources/", "/website/"];

        private static readonly string[] ForwardedRequestHeaders =
            ["Range", "If-Range", "If-None-Match", "If-Modified-Since", "Accept", "User-Agent"];

        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = TimeSpan.FromMinutes(30) };

        private static readonly string CdnOrigin = Environment.GetEnvironmentVariable("GRIFFIN_CDN_ORIGIN")?.TrimEnd('/')
            ?? new Uri(StaticConfig.ResUrlCdn).GetLeftPart(UriPartial.Authority);

        public static bool Matches(PathString path) =>
            ProxiedPrefixes.Any(prefix => path.StartsWithSegments(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        public async Task ForwardAsync(HttpContext context)
        {
            string target = CdnOrigin + context.Request.Path + context.Request.QueryString;
            using HttpRequestMessage upstreamRequest = new(
                HttpMethods.IsHead(context.Request.Method) ? HttpMethod.Head : HttpMethod.Get, target);

            foreach (string header in ForwardedRequestHeaders)
            {
                if (context.Request.Headers.TryGetValue(header, out var values))
                    upstreamRequest.Headers.TryAddWithoutValidation(header, values.ToArray());
            }

            try
            {
                using HttpResponseMessage upstream = await Client.SendAsync(
                    upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                context.Response.StatusCode = (int)upstream.StatusCode;
                CopyHeaders(upstream.Headers, context.Response);
                CopyHeaders(upstream.Content.Headers, context.Response);
                context.Response.Headers.Remove("Transfer-Encoding");
                context.Response.Headers.Connection = "close";

                long? length = upstream.Content.Headers.ContentLength;
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await using Stream body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
                    await body.CopyToAsync(context.Response.Body, context.RequestAborted);
                }

                logger.LogInformation("CDN proxy {Method} {Path} range={Range} -> {Status} bytes={Bytes}",
                    context.Request.Method, context.Request.Path, context.Request.Headers.Range.ToString(),
                    (int)upstream.StatusCode, length?.ToString() ?? "?");
            }
            catch (OperationCanceledException)
            {
                // client aborted the download
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CDN proxy failed for {Target}", target);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    context.Response.Headers.Connection = "close";
                }
            }
        }

        private static void CopyHeaders(HttpHeaders source, HttpResponse response)
        {
            foreach (var header in source)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                    continue;

                response.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
