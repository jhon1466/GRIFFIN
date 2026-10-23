namespace Griffin.Common.Utils
{
    public static class StaticConfig
    {
        // The SDK gatelist and TCP listener must announce/use the same endpoint.
        // GRIFFIN_* overrides remain available for local test instances.
        public static readonly string GameServerHost = Env("GRIFFIN_GAME_SERVER_HOST", Env("GameServer__Host", "127.0.0.1"));

        public static readonly int GameServerPort = int.TryParse(Env("GRIFFIN_GAME_SERVER_PORT", Env("GameServer__Port", "7001")), out var port) ? port : 7001;

        public static string GameServerAddress => $"{GameServerHost}:{GameServerPort}";

        public static string ResourceDir = Path.Join(Path.GetDirectoryName(AppContext.BaseDirectory), "Resources");

        public static string PcapDir = Path.Join(ResourceDir, "Packets");

        // Versions announced to the client. Defaults match the official CN live values observed on 2026-10-14
        // (zone/client_res_v1) and the installed client (launcher f_r_ab_v / Table/version.txt).
        // Each can be overridden with an GRIFFIN_* environment variable without rebuilding.
        public static readonly string ClientVersion = Env("GRIFFIN_CLIENT_VERSION", "4.0.5136.0.0");

        public static readonly string AbResourceVersion = Env("GRIFFIN_AB_RESOURCE_VERSION", "4.0.5136.14911.28655");

        public static readonly string GameClientVersion = Env("GRIFFIN_GAME_CLIENT_VERSION", "4.0.5136");

        /// <summary>Version of the server-side table set (bk_stc_pb.zip). The client downloads this version from the CDN.</summary>
        public static readonly string StcVersion = Env("GRIFFIN_STC_VERSION", "1194912");

        public const string ResUrlCdn = "https://gf2-cn.cdn.sunborngame.com/game_resources";

        public const string ResUrlOss = "https://gf2.oss-cn-beijing.aliyuncs.com/game_resources";

        public static string StcTableZipUrl => $"{ResUrlCdn}/data/{StcVersion}/bk_stc_pb.zip";

        public static readonly string BinaryVersion = Env("GRIFFIN_BINARY_VERSION", "4.0.5136.3257");

        public const string MustUpdateVersionClient = "13407.25794";

        public const string MustUpdateVersionBin = "2825";

        public const string MustUpdateVersionStc = "1128281";

        public const int GameNoticeListVersion = 1001;

        private static string Env(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
