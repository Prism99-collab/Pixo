using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>
/// The Claude desktop app's own sign-in, for the AI usage page's limits when Claude Code was never signed in from a
/// terminal. The app keeps it in its config.json ("oauth:tokenCacheV2"), encrypted the way every Chromium-based app
/// does: a key in its "Local State" protected for this Windows account (DPAPI), the value sealed with AES-GCM ("v10").
/// Pixo runs as the same account, so it can open it, as the app itself does.
/// The token is only kept in memory for the one request to Anthropic's usage address: never written, never logged,
/// never renewed (an expired one is simply skipped). Store version: %LOCALAPPDATA%\Claude-Data\…; installer version:
/// %APPDATA%\Claude.
/// </summary>
public static class ClaudeDesktopSignIn
{
    public sealed record SignIn(string Token, string? Plan);

    static IEnumerable<string> Folders()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(data)) foreach (var d in Directory.GetDirectories(data)) yield return d;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
    }

    /// <summary>Is the desktop app signed in on this PC (without opening anything)?</summary>
    public static bool Present => Folders().Any(d => File.Exists(Path.Combine(d, "config.json")) && File.Exists(Path.Combine(d, "Local State")));

    /// <summary>The freshest sign-in that can read the profile (the usage needs it), or null.</summary>
    public static SignIn? Read()
    {
        foreach (var dir in Folders())
        {
            try
            {
                string cfgFile = Path.Combine(dir, "config.json"), stateFile = Path.Combine(dir, "Local State");
                if (!File.Exists(cfgFile) || !File.Exists(stateFile)) continue;
                var cfg = JsonNode.Parse(File.ReadAllText(cfgFile));
                var sealedKey = Convert.FromBase64String((string?)JsonNode.Parse(File.ReadAllText(stateFile))?["os_crypt"]?["encrypted_key"] ?? "");
                if (sealedKey.Length < 6 || Encoding.ASCII.GetString(sealedKey, 0, 5) != "DPAPI") continue;
                var key = OpenRouter.Unseal(sealedKey[5..]);
                if (key is not { Length: 32 }) continue;
                foreach (var name in new[] { "oauth:tokenCacheV2", "oauth:tokenCache" })
                {
                    if ((string?)cfg?[name] is not { Length: > 0 } b64 || Open(key, Convert.FromBase64String(b64)) is not { } text) continue;
                    if (Pick(JsonNode.Parse(text)) is { } signIn) return signIn;
                }
            }
            catch (Exception ex) { AppLog.Write("ai usage: Claude desktop's sign-in not readable (" + ex.GetType().Name + ")"); }
        }
        return null;
    }

    /// <summary>"v10" + a 12-byte nonce + the sealed text + a 16-byte tag (AES-256-GCM).</summary>
    static string? Open(byte[] key, byte[] raw)
    {
        if (raw.Length < 3 + 12 + 16 || raw[0] != 'v' || raw[1] != '1') return null;
        var plain = new byte[raw.Length - 31];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(raw.AsSpan(3, 12), raw.AsSpan(15, plain.Length), raw.AsSpan(raw.Length - 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// The cache holds one entry per sign-in, keyed by account and scopes: the one not about to expire, with the
    /// profile scope, that lasts longest.
    /// </summary>
    public static SignIn? Pick(JsonNode? cache, DateTime? nowUtc = null)
    {
        if (cache is not JsonObject all) return null;
        long now = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds() + 60_000;
        return all
            .Where(kv => kv.Key.Contains("user:profile") && kv.Value is JsonObject)
            .Select(kv => (Token: (string?)kv.Value!["token"], Exp: (long?)kv.Value!["expiresAt"] ?? 0, Plan: (string?)kv.Value!["subscriptionType"]))
            .Where(e => !string.IsNullOrEmpty(e.Token) && e.Exp > now)
            .OrderByDescending(e => e.Exp)
            .Select(e => new SignIn(e.Token!, e.Plan))
            .FirstOrDefault();
    }
}
