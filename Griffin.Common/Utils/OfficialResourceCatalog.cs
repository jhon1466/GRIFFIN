using System.Collections.Concurrent;

namespace Griffin.Common.Utils;

/// <summary>
/// Holds official resource versions that are safe to announce to clients already supported by this server.
/// Resource bundles can update independently; table and binary versions remain pinned to the server profile.
/// </summary>
public static class OfficialResourceCatalog
{
    private static readonly ConcurrentDictionary<string, string> ResourceVersionsByClient = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> PreDownloadVersionsByClient = new(StringComparer.Ordinal);

    public static bool TryPublish(string clientVersion, string resourceVersion, int preDownloadVersion)
    {
        string clientKey = ClientKey(clientVersion);
        if (string.IsNullOrWhiteSpace(clientKey) || !string.Equals(BuildOf(clientVersion), BuildOf(resourceVersion), StringComparison.Ordinal))
            return false;

        ResourceVersionsByClient[clientKey] = resourceVersion;
        PreDownloadVersionsByClient[clientKey] = preDownloadVersion;
        return true;
    }

    public static string Resolve(string clientVersion, string configuredVersion)
    {
        // An explicit operator pin always wins, including for rollback and compatibility testing.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRIFFIN_AB_RESOURCE_VERSION")))
            return configuredVersion;

        return ResourceVersionsByClient.TryGetValue(ClientKey(clientVersion), out string? official)
            ? official
            : configuredVersion;
    }

    public static int ResolvePreDownloadVersion(string clientVersion, int configuredVersion)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRIFFIN_AB_RESOURCE_VERSION")))
            return configuredVersion;

        return PreDownloadVersionsByClient.TryGetValue(ClientKey(clientVersion), out int official)
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

    private static string ClientKey(string? version) =>
        string.Equals(BuildOf(version), BuildOf(StaticConfig.AndroidClientVersion), StringComparison.Ordinal)
            ? StaticConfig.AndroidClientVersion
            : StaticConfig.ClientVersion;
}
