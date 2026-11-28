using System.Globalization;

namespace DynamicNotch.Services;

/// <summary>
/// The Converter page's units: eight categories, each unit a factor to its category's base unit (metre, kilogram,
/// litre, metre per second, square metre, byte, second); temperature is the one that needs its own formulas.
/// Numbers are read with a comma or a dot for decimals and shown with up to six significant digits.
/// </summary>
public static class Converter
{
    public sealed record Unit(string Key, string Name, double Factor);
    public sealed record Category(string Key, Unit[] Units, string From, string To);

    public static readonly Category[] Categories =
    {
        new("Length", new Unit[]
        {
            new("mm", "Millimetre", 0.001), new("cm", "Centimetre", 0.01), new("m", "Metre", 1), new("km", "Kilometre", 1000),
            new("in", "Inch", 0.0254), new("ft", "Foot", 0.3048), new("yd", "Yard", 0.9144), new("mi", "Mile", 1609.344), new("nmi", "Nautical mile", 1852),
        }, "cm", "in"),
        new("Weight", new Unit[]
        {
            new("mg", "Milligram", 1e-6), new("g", "Gram", 0.001), new("kg", "Kilogram", 1), new("t", "Tonne", 1000),
            new("oz", "Ounce", 0.028349523125), new("lb", "Pound", 0.45359237), new("st", "Stone", 6.35029318),
        }, "kg", "lb"),
        new("Temperature", new Unit[] { new("C", "Celsius", 1), new("F", "Fahrenheit", 1), new("K", "Kelvin", 1) }, "C", "F"),
        new("Volume", new Unit[]
        {
            new("ml", "Millilitre", 0.001), new("cl", "Centilitre", 0.01), new("l", "Litre", 1), new("m3", "Cubic metre", 1000),
            new("tsp", "Teaspoon", 0.00492892159375), new("tbsp", "Tablespoon", 0.01478676478125), new("floz", "Fluid ounce (US)", 0.0295735295625),
            new("cup", "Cup (US)", 0.2365882365), new("pt", "Pint (US)", 0.473176473), new("gal", "Gallon (US)", 3.785411784), new("galuk", "Gallon (UK)", 4.54609),
        }, "l", "gal"),
        new("Speed", new Unit[]
        {
            new("ms", "Metre per second", 1), new("kmh", "Kilometre per hour", 1 / 3.6), new("mph", "Mile per hour", 0.44704),
            new("kn", "Knot", 1852 / 3600.0), new("fts", "Foot per second", 0.3048),
        }, "kmh", "mph"),
        new("Area", new Unit[]
        {
            new("mm2", "Square millimetre", 1e-6), new("cm2", "Square centimetre", 1e-4), new("m2", "Square metre", 1), new("ha", "Hectare", 1e4),
            new("km2", "Square kilometre", 1e6), new("in2", "Square inch", 0.00064516), new("ft2", "Square foot", 0.09290304),
            new("yd2", "Square yard", 0.83612736), new("ac", "Acre", 4046.8564224), new("mi2", "Square mile", 2589988.110336),
        }, "m2", "ft2"),
        new("Data", new Unit[]
        {
            new("bit", "Bit", 0.125), new("B", "Byte", 1), new("KB", "Kilobyte", 1e3), new("MB", "Megabyte", 1e6), new("GB", "Gigabyte", 1e9),
            new("TB", "Terabyte", 1e12), new("KiB", "Kibibyte", 1024), new("MiB", "Mebibyte", 1048576), new("GiB", "Gibibyte", 1073741824),
            new("TiB", "Tebibyte", 1099511627776), new("Mbit", "Megabit", 125000),
        }, "GB", "GiB"),
        new("Time", new Unit[]
        {
            new("ms", "Millisecond", 0.001), new("s", "Second", 1), new("min", "Minute", 60), new("h", "Hour", 3600), new("d", "Day", 86400),
            new("wk", "Week", 604800), new("mo", "Month", 2629800), new("yr", "Year", 31557600), // (a month: 30.44 days; a year: 365.25)
        }, "h", "min"),
    };

    public static Category? Find(string cat) => Categories.FirstOrDefault(c => c.Key == cat);
    public static Unit? Find(string cat, string unit) => Find(cat)?.Units.FirstOrDefault(u => u.Key == unit);

    public static double Convert(string cat, string from, string to, double v)
    {
        if (cat == "Temperature")
        {
            double c = from switch { "F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
            return to switch { "F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
        }
        if (Find(cat, from) is not { } f || Find(cat, to) is not { } t) return double.NaN;
        return v * f.Factor / t.Factor;
    }

    /// <summary>"1,5", "1.5", "1 234,5", "1,234.5" and "-3" all read; anything else is not a number.</summary>
    public static bool TryParse(string? s, out double v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Arabic-Indic digits and the Arabic decimal sign, spaces of any kind (French groups with U+202F) and underscores
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s.Trim())
        {
            if (ch is >= '\u0660' and <= '\u0669') sb.Append((char)('0' + ch - '\u0660'));
            else if (ch is >= '\u06F0' and <= '\u06F9') sb.Append((char)('0' + ch - '\u06F0'));
            else if (ch == '\u066B') sb.Append('.');
            else if (ch is '\u066C' or '_' or '\'' || char.IsWhiteSpace(ch)) continue;
            else sb.Append(ch);
        }
        var t = sb.ToString();
        int comma = t.LastIndexOf(','), dot = t.LastIndexOf('.');
        // both marks: the last one is the decimal ("1,234.5", "1.234,5"); one mark used twice groups thousands ("1.234.567")
        if (comma >= 0 && dot >= 0) t = comma > dot ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (t.Count(ch => ch == ',') > 1) t = t.Replace(",", "");
        else if (t.Count(ch => ch == '.') > 1) t = t.Replace(".", "");
        else t = t.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
    }

    /// <summary>Up to six significant digits, no trailing zeros, a dot for decimals (very large or tiny: with an exponent).</summary>
    public static string Format(double v)
    {
        if (!double.IsFinite(v)) return "";
        double a = Math.Abs(v);
        if (a == 0) return "0";
        if (a >= 1e15 || a < 1e-6) return v.ToString("G6", CultureInfo.InvariantCulture);
        int decimals = Math.Clamp(6 - ((int)Math.Floor(Math.Log10(a)) + 1), 0, 12);
        return Math.Round(v, decimals).ToString("0.############", CultureInfo.InvariantCulture);
    }
}
