using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Griffin.Common.Manager;
using Griffin.SDKServer.Controllers;
using Griffin.SDKServer.Services;

namespace Griffin.SDKServer
{
    public static class SdkServerExtensions
    {
        public static IServiceCollection AddSdkServer(this IServiceCollection services)
        {
            services.AddScoped<IAuthService, AuthService>();

            services.AddControllers()
                    .AddApplicationPart(typeof(SDKController).Assembly);

            // The game's libcurl only needs HTTP/1.1; offering HTTP/2 via ALPN is an extra way for the transfer to stall.
            // Set GRIFFIN_HTTP2=1 to allow it again.
            if (Environment.GetEnvironmentVariable("GRIFFIN_HTTP2") != "1")
            {
                services.Configure<KestrelServerOptions>(options =>
                    options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http1));
            }

            services.AddSingleton<CdnProxy>();
            services.AddTransient<IStartupFilter, SdkHttpStartupFilter>();

            return services;
        }
    }
}
