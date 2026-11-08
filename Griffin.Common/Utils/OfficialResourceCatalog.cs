using System.Collections.Concurrent;

namespace Griffin.Common.Utils;

/// <summary>
/// Holds official resource versions that are safe to announce to clients already supported by this server.
/// Resource bundles can update independently; table and binary versions remain pinned to the server profile.
/// </summary>
public static class OfficialResourceCatalog
{
    private static readonly ConcurrentDictionary<string, string> ResourceVersionsByBuild = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> PreDownloadVersionsByBuild = new(StringComparer.Ordinal);

    /// <summary>
    /// The last official versions seen, kept in Resources/official-versions.json (keyed by client build). Without them a start during
    /// which the official service answered late announced the old built-in versions, and the client downloaded every resource and table
    /// again, then once more when it went back to the official server.
    /// </summary>
    private sealed class Remembered
    {
        public Dictionary<string, string> Res { get; set; } = [];
        public Dictionary<string, int> PreDownload { get; set; } = [];
        public string? Stc { get; set; }
    }

    private static readonly object FileGate = new();

    private static string FilePath => Path.Combine(StaticConfig.ResourceDir, "official-versions.json");

    static OfficialResourceCatalog()
    {
        try
        {
            if (File.Exists(FilePath) && System.Text.Json.JsonSerializer.Deserialize<Remembered>(File.ReadAllText(FilePath)) is { } saved)
            {
                foreach ((string build, string res) in saved.Res)
                    ResourceVersionsByBuild[BuildOf(build)] = res;
                foreach ((string build, int pre) in saved.PreDownload)
                    PreDownloadVersionsByBuild[BuildOf(build)] = pre;
                RememberedStc = saved.Stc;
            }
        }
        catch (Exception)
        {
            // a damaged file only means starting from the built-in versions
        }
    }

    /// <summary>The official table version seen last time (null when never seen).</summary>
    public static string? RememberedStc { get; private set; }

    public static void RememberStc(string stc)
    {
        RememberedStc = stc;
        Persist();
    }

    private static void Persist()
    {
        lock (FileGate)
        {
            try
            {
                Remembered saved = new()
                {
                    Res = new Dictionary<string, string>(ResourceVersionsByBuild),
                    PreDownload = new Dictionary<string, int>(PreDownloadVersionsByBuild),
                    Stc = RememberedStc,
                };
                Directory.CreateDirectory(StaticConfig.ResourceDir);
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(saved));
            }
            catch (Exception)
            {
                // remembering is a convenience; the live answer still applies
            }
        }
    }

    public static bool TryPublish(string clientVersion, string resourceVersion, int preDownloadVersion)
    {
        string build = BuildOf(clientVersion);
        if (string.IsNullOrWhiteSpace(build) || !string.Equals(build, BuildOf(resourceVersion), StringComparison.Ordinal))
            return false;

        bool changed = !ResourceVersionsByBuild.TryGetValue(build, out string? before) || before != resourceVersion
            || !PreDownloadVersionsByBuild.TryGetValue(build, out int pre) || pre != preDownloadVersion;
        ResourceVersionsByBuild[build] = resourceVersion;
        PreDownloadVersionsByBuild[build] = preDownloadVersion;
        if (changed)
            Persist();
        return true;
    }

    public static string Resolve(string clientVersion, string configuredVersion)
    {
        // An explicit operator pin always wins, including for rollback and compatibility testing.
        string pin = StaticConfig.IsAndroidVersion(clientVersion) ? "GRIFFIN_ANDROID_AB_RESOURCE_VERSION" : "GRIFFIN_AB_RESOURCE_VERSION";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(pin)))
            return configuredVersion;

        return ResourceVersionsByBuild.TryGetValue(BuildOf(clientVersion), out string? official)
            ? official
            : configuredVersion;
    }

    public static int ResolvePreDownloadVersion(string clientVersion, int configuredVersion)
    {
        string pin = StaticConfig.IsAndroidVersion(clientVersion) ? "GRIFFIN_ANDROID_AB_RESOURCE_VERSION" : "GRIFFIN_AB_RESOURCE_VERSION";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(pin)))
            return configuredVersion;

        return PreDownloadVersionsByBuild.TryGetValue(BuildOf(clientVersion), out int official)
            ? official
            : configuredVersion;
    }

    private static string BuildOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        string[] parts = version.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : string.Empty;
    }
}
