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

        // Versions announced to the client. Defaults match the official CN live values observed on 2026-11-03
        // (zone/client_res_v1); the server profile targets the same 4.0.5136 client build.
        // Each can be overridden with an GRIFFIN_* environment variable without rebuilding.
        public static readonly string ClientVersion = Env("GRIFFIN_CLIENT_VERSION", "4.0.5136.0.0");

        public static readonly string AbResourceVersion = Env("GRIFFIN_AB_RESOURCE_VERSION", "4.0.5136.15094.29138");

        public static readonly string GameClientVersion = Env("GRIFFIN_GAME_CLIENT_VERSION", "4.0.5136");

        /// <summary>Version of the server-side table set (bk_stc_pb.zip). The client downloads this version from the CDN.</summary>
        /// <remarks>
        /// At start the server follows the table version of the official servers (<see cref="OfficialStcVersionUrl"/>), so the client keeps the
        /// tables it downloads from the official CDN instead of swapping them every time it switches between the official and the private
        /// server. <see cref="PinnedStcVersion"/> is the fallback when the official service cannot be reached or its tables do not parse;
        /// GRIFFIN_STC_VERSION pins a version and disables the follow.
        /// </remarks>
        public static string StcVersion { get; set; } = Env("GRIFFIN_STC_VERSION", "1201382");

        public const string PinnedStcVersion = "1201382";

        public static bool StcVersionPinnedByEnv => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRIFFIN_STC_VERSION"));

        public static string OfficialStcVersionUrl =>
            $"https://gf2-zoneinfo.sunborngame.com/zone/client_res_v1?type_id=3&version={Uri.EscapeDataString(ClientVersion)}&game_channel_id=5";

        public const string ResUrlCdn = "https://gf2-cn.cdn.sunborngame.com/game_resources";

        public const string ResUrlOss = "https://gf2.oss-cn-beijing.aliyuncs.com/game_resources";

        public static string StcTableZipUrl => $"{ResUrlCdn}/data/{StcVersion}/bk_stc_pb.zip";

        public static readonly string BinaryVersion = Env("GRIFFIN_BINARY_VERSION", "4.0.5136.3320");

        // Forced-update thresholds. Must be lower than any client version in use (or "0") so clients
        // are not rejected with "当前客户端版本已过时" (force update dialog).
        public static readonly string MustUpdateVersionClient = Env("GRIFFIN_MUST_UPDATE_VERSION_CLIENT", "0");

        public static readonly string MustUpdateVersionBin = Env("GRIFFIN_MUST_UPDATE_VERSION_BIN", "0");

        public static readonly string MustUpdateVersionStc = Env("GRIFFIN_MUST_UPDATE_VERSION_STC", "0");

        // Android (CN APK) is a separate build with its own resource numbers: the AssetBundles of Android live in a
        // different folder (29001) than Windows (29000) and an empty BinaryVersion breaks its parser ("解析错误提示").
        // The SDK server picks this profile when the client asks with the Android build in "version" (A-174).
        public static readonly string AndroidClientVersion = Env("GRIFFIN_ANDROID_CLIENT_VERSION", "4.0.5137.0.0");

        public static readonly string AndroidAbResourceVersion = Env("GRIFFIN_ANDROID_AB_RESOURCE_VERSION", "4.0.5137.15094.29139");

        public static readonly string AndroidBinaryVersion = Env("GRIFFIN_ANDROID_BINARY_VERSION", "4.0.5137.3320");

        public static readonly string AndroidGameClientVersion = Env("GRIFFIN_ANDROID_GAME_CLIENT_VERSION", "4.0.5137");

        /// <summary>True when a "version" query (4.0.&lt;build&gt;.x.y) belongs to the Android build.</summary>
        public static bool IsAndroidVersion(string? version) =>
            !string.IsNullOrEmpty(version) && BuildOf(version) == BuildOf(AndroidClientVersion) && BuildOf(version) != BuildOf(ClientVersion);

        private static string BuildOf(string version)
        {
            string[] parts = version.Split('.');
            return parts.Length > 2 ? parts[2] : version;
        }

        public const int GameNoticeListVersion = 1001;

        private static string Env(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
