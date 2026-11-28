using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicNotch.Services;

/// <summary>
/// The Weather page's data: Open-Meteo (free, no key, CC BY 4.0) for the forecast and for finding a place by name.
/// The place is typed once by you (no location lookup, no guess from the internet address), and kept with the last
/// forecast in weather.json, so the page shows something at once, even offline. Temperatures come in °C and wind in
/// km/h; °F and mph are worked out here, so switching never asks the internet again.
/// </summary>
public static class WeatherService
{
    public enum Kind { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Storm }

    public sealed record Place(string Name, string? Region, string? Country, double Lat, double Lon);
    public sealed record Now(double Temp, double Feels, int Humidity, double Wind, int Code, bool IsDay);
    public sealed record Hour(DateTime At, double Temp, int Code, int Rain, bool IsDay);
    public sealed record Day(DateTime Date, int Code, double Max, double Min, int Rain);
    public sealed record Forecast(Now Current, List<Hour> Hours, List<Day> Days, DateTime FetchedUtc);

    public sealed class State
    {
        public Place? Place { get; set; }
        /// <summary>Null: as the Windows region says (°F in the few countries that use it).</summary>
        public bool? Fahrenheit { get; set; }
        public Forecast? Last { get; set; }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string FilePath => Path.Combine(Settings.Folder, "weather.json");

    public static State Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) { AppLog.Write("weather: weather.json not readable: " + ex.Message); return new(); }
    }

    public static void Save(State s)
    {
        try { SafeWrite(FilePath, JsonSerializer.Serialize(s)); }
        catch (Exception ex) { AppLog.Write("weather: not saved: " + ex.Message); }
    }

    /// <summary>A file written whole or not at all: a temp file of its own (two writers never share one), then a rename.</summary>
    public static void SafeWrite(string path, string text)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static bool UsesFahrenheit(State s) => s.Fahrenheit ?? !RegionInfo.CurrentRegion.IsMetric;

    public static double ToF(double c) => c * 9 / 5 + 32;

    /// <summary>A WMO weather code (what Open-Meteo gives) as words and as one of the drawn icons.</summary>
    public static (string Label, Kind Kind) Describe(int code) => code switch
    {
        0 => ("Clear", Kind.Clear),
        1 => ("Mostly clear", Kind.Clear),
        2 => ("Partly cloudy", Kind.PartlyCloudy),
        3 => ("Cloudy", Kind.Cloudy),
        45 or 48 => ("Fog", Kind.Fog),
        >= 51 and <= 57 => ("Drizzle", Kind.Rain),
        >= 61 and <= 67 => ("Rain", Kind.Rain),
        >= 71 and <= 77 => ("Snow", Kind.Snow),
        >= 80 and <= 82 => ("Showers", Kind.Rain),
        85 or 86 => ("Snow showers", Kind.Snow),
        >= 95 => ("Thunderstorm", Kind.Storm),
        _ => ("Cloudy", Kind.Cloudy),
    };

    /// <summary>Places that match a name (up to five), in your language when Open-Meteo has it.</summary>
    public static async Task<List<Place>> SearchAsync(string name, CancellationToken ct)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=5&language={Loc.Lang}&format=json";
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var list = new List<Place>();
        foreach (var r in root?["results"]?.AsArray() ?? new JsonArray())
        {
            if (r?["latitude"] is null || r["longitude"] is null) continue;
            list.Add(new Place((string?)r["name"] ?? name, (string?)r["admin1"], (string?)r["country"], (double)r["latitude"]!, (double)r["longitude"]!));
        }
        return list;
    }

    public static async Task<Forecast> FetchAsync(Place p, CancellationToken ct)
    {
        var url = "https://api.open-meteo.com/v1/forecast?" + FormattableString.Invariant($"latitude={p.Lat:0.####}&longitude={p.Lon:0.####}")
            + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day"
            + "&hourly=temperature_2m,weather_code,precipitation_probability,is_day"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
            + "&timezone=auto&forecast_days=7";
        return Parse(await Http.GetStringAsync(url, ct));
    }

    /// <summary>Open-Meteo's answer: now, the next 24 hours (from the current hour) and the 7 days.</summary>
    public static Forecast Parse(string json)
    {
        var root = JsonNode.Parse(json)!;
        var c = root["current"]!;
        static DateTime T(JsonNode? n) => DateTime.Parse((string)n!, CultureInfo.InvariantCulture);
        static int I(JsonNode? n) => n is null ? 0 : (int)Math.Round((double)n);
        static double D(JsonNode? n) => n is null ? 0 : (double)n;
        var now = new Now(D(c["temperature_2m"]), D(c["apparent_temperature"]), I(c["relative_humidity_2m"]), D(c["wind_speed_10m"]), I(c["weather_code"]), I(c["is_day"]) == 1);
        var at = T(c["time"]);
        var from = new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0);

        var h = root["hourly"]!;
        var times = h["time"]!.AsArray();
        var hours = new List<Hour>();
        for (int i = 0; i < times.Count && hours.Count < 24; i++)
        {
            var t = T(times[i]);
            if (t < from) continue;
            hours.Add(new Hour(t, D(h["temperature_2m"]![i]), I(h["weather_code"]![i]), I(h["precipitation_probability"]?[i]), I(h["is_day"]?[i]) == 1));
        }

        var d = root["daily"]!;
        var dt = d["time"]!.AsArray();
        var days = new List<Day>();
        for (int i = 0; i < dt.Count; i++)
            days.Add(new Day(T(dt[i]), I(d["weather_code"]![i]), D(d["temperature_2m_max"]![i]), D(d["temperature_2m_min"]![i]), I(d["precipitation_probability_max"]?[i])));
        return new Forecast(now, hours, days, DateTime.UtcNow);
    }
}
