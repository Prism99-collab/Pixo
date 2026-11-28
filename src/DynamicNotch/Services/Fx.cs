using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>
/// Currency rates for the Converter page: the European Central Bank's daily reference rates through Frankfurter (free,
/// no key). Asked only when the Currency side is opened, at most every 6 hours; kept in fx.json, so the last rates
/// (and their date) still work offline.
/// </summary>
public static class Fx
{
    public sealed record Rates(string Date, Dictionary<string, double> PerEur, DateTime FetchedUtc);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly string[] Sources = { "https://api.frankfurter.dev/v1/latest?base=EUR", "https://api.frankfurter.app/latest" };
    static string FilePath => Path.Combine(Settings.Folder, "fx.json");
    static Rates? _rates;
    static Task<Rates?>? _asking;

    public static Rates? Known => _rates ??= Load();

    static Rates? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Rates>(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) { AppLog.Write("fx: saved rates unreadable: " + ex.Message); return null; }
    }

    /// <summary>The rates: the saved ones while fresh (6 h), otherwise asked again (the saved ones if that fails).</summary>
    public static Task<Rates?> GetAsync()
    {
        if (Known is { } k && DateTime.UtcNow - k.FetchedUtc < TimeSpan.FromHours(6)) return Task.FromResult<Rates?>(k);
        if (_asking is { IsCompleted: true }) _asking = null; // (a finished ask: the next one asks again)
        return _asking ??= Task.Run(async () => await AskAsync() ?? Known);
    }

    static async Task<Rates?> AskAsync()
    {
        foreach (var url in Sources)
        {
            try
            {
                var j = JsonNode.Parse(await Http.GetStringAsync(url));
                if (j?["rates"] is not JsonObject list || list.Count == 0) continue;
                var per = new Dictionary<string, double> { ["EUR"] = 1 };
                foreach (var (code, value) in list)
                    if (code.Length == 3 && value?.GetValue<double>() is double d && d > 0) per[code] = d;
                var r = new Rates(j["date"]?.GetValue<string>() ?? "", per, DateTime.UtcNow);
                _rates = r;
                try { WeatherService.SafeWrite(FilePath, JsonSerializer.Serialize(r)); } catch { }
                return r;
            }
            catch (Exception ex) { AppLog.Write($"fx: {new Uri(url).Host}: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>An amount from one currency to another, through the euro (null: a code it has no rate for).</summary>
    public static double? Convert(Rates r, string from, string to, double v) =>
        r.PerEur.TryGetValue(from, out var f) && r.PerEur.TryGetValue(to, out var t) ? v / f * t : null;
}
