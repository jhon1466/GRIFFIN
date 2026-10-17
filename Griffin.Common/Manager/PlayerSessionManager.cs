using Griffin.Common.Utils;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Griffin.Common.Manager
{
    public class PlayerSessionManager : Singleton<PlayerSessionManager>
    {
        // Persisted so tokens cached by the client stay valid across server restarts (reconnect after restart).
        // GRIFFIN_SESSION_KEY (base64, 32 bytes) overrides the file Resources/session.key. Never commit this file.
        private static readonly byte[] SigningKey = LoadOrCreateSigningKey();

        private readonly ConcurrentDictionary<string, PlayerSession> _sessions = new();

        public string CreateSession(uint uid, bool isGuest)
        {
            var token = GenerateAccessToken(uid, isGuest);
            var session = new PlayerSession(uid, token, DateTime.UtcNow);

            _sessions[token] = session;
            return token;
        }

        public PlayerSession? GetSession(string accessToken)
        {
            if (!VerifyAccessToken(accessToken))
                return null;

            if (_sessions.TryGetValue(accessToken, out var session))
                return session;

            // Valid signature but unknown in memory (server restarted): rebuild the session from the signed payload.
            var payload = ReadPayload(accessToken);
            if (payload is null || payload.Uid == 0 || payload.Expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return null;

            return _sessions.GetOrAdd(accessToken, new PlayerSession(payload.Uid, accessToken, DateTime.UtcNow));
        }

        private static AccessTokenPayload? ReadPayload(string accessToken)
        {
            try
            {
                var payloadBase64 = accessToken[..accessToken.IndexOf('.')];
                return JsonSerializer.Deserialize<AccessTokenPayload>(Convert.FromBase64String(payloadBase64));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] LoadOrCreateSigningKey()
        {
            var fromEnv = Environment.GetEnvironmentVariable("GRIFFIN_SESSION_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                var key = Convert.FromBase64String(fromEnv.Trim());
                if (key.Length < 32)
                    throw new InvalidOperationException("GRIFFIN_SESSION_KEY must be base64 of at least 32 bytes.");
                return key;
            }

            var path = Path.Combine(StaticConfig.ResourceDir, "session.key");
            try
            {
                if (File.Exists(path))
                {
                    var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                    if (existing.Length >= 32)
                        return existing;
                }

                Directory.CreateDirectory(StaticConfig.ResourceDir);
                var created = RandomNumberGenerator.GetBytes(32);
                File.WriteAllText(path, Convert.ToBase64String(created));
                return created;
            }
            catch (Exception)
            {
                // Read-only or unusable location: fall back to an in-memory key (tokens then die with the process).
                return RandomNumberGenerator.GetBytes(32);
            }
        }

        public void RemoveSession(string accessToken)
        {
            _sessions.TryRemove(accessToken, out _);
        }

        private static string GenerateAccessToken(uint uid, bool isGuest)
        {
            var openId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var expires = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();

            var payload = new AccessTokenPayload
            {
                OpenId = openId,
                Expires = expires,
                IsGuest = isGuest ? 1 : 0,
                Uid = uid,
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));

            var signature = HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(payloadBase64));
            var signatureBase64 = Convert.ToBase64String(signature);

            return $"{payloadBase64}.{signatureBase64}";
        }

        private static bool VerifyAccessToken(string accessToken)
        {
            var dotIndex = accessToken.IndexOf('.');
            if (dotIndex <= 0 || dotIndex >= accessToken.Length - 1)
                return false;

            var payloadBase64 = accessToken[..dotIndex];
            var signatureBase64 = accessToken[(dotIndex + 1)..];

            byte[] expectedSignature;
            try
            {
                expectedSignature = HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(payloadBase64));
            }
            catch
            {
                return false;
            }

            byte[] actualSignature;
            try
            {
                actualSignature = Convert.FromBase64String(signatureBase64);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature);
        }

        private class AccessTokenPayload
        {
            [JsonPropertyName("openid")]
            public string OpenId { get; set; } = string.Empty;

            [JsonPropertyName("expires")]
            public long Expires { get; set; }

            [JsonPropertyName("is_guest")]
            public int IsGuest { get; set; }

            [JsonPropertyName("uid")]
            public uint Uid { get; set; }
        }
    }

    public record PlayerSession(uint Uid, string AccessToken, DateTime CreatedAt);
}
