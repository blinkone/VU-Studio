using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VuStudio.Core.Lighting;

/// <summary>
/// A backlight color: RGB 0..255 plus the separate white LED 0..255.
/// Serialized as <c>"#RRGGBB"</c> or <c>"#RRGGBB+WW"</c> (WW = white, hex) to stay readable in JSON.
/// </summary>
[JsonConverter(typeof(RgbwColorJsonConverter))]
public readonly record struct RgbwColor(byte R, byte G, byte B, byte W = 0)
{
    public static readonly RgbwColor Black = new(0, 0, 0);
    public static readonly RgbwColor White = new(255, 255, 255);
    public static readonly RgbwColor Red = new(255, 0, 0);
    public static readonly RgbwColor Orange = new(255, 120, 0);
    public static readonly RgbwColor Yellow = new(255, 210, 0);
    public static readonly RgbwColor Green = new(0, 255, 60);
    public static readonly RgbwColor Cyan = new(0, 220, 255);
    public static readonly RgbwColor Blue = new(0, 80, 255);
    public static readonly RgbwColor WarmWhite = new(255, 170, 90, 120);

    [JsonIgnore]
    public bool IsOff => R == 0 && G == 0 && B == 0 && W == 0;

    public string ToHex() => W == 0 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}+{W:X2}";

    public override string ToString() => ToHex();

    public static RgbwColor Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"Invalid color '{text}'.");

    /// <summary>Accepts <c>#RGB</c>, <c>#RRGGBB</c>, <c>#RRGGBB+WW</c> and <c>#RRGGBBWW</c>-less variants without '#'.</summary>
    public static bool TryParse(string? text, out RgbwColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().TrimStart('#');
        byte w = 0;
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            if (!byte.TryParse(s[(plus + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out w))
            {
                return false;
            }

            s = s[..plus];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new RgbwColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, w);
        return true;
    }

    public static RgbwColor Lerp(RgbwColor a, RgbwColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbwColor(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t), Mix(a.W, b.W, t));

        static byte Mix(byte x, byte y, double t) => (byte)Math.Round(x + ((y - x) * t));
    }

    public RgbwColor Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new RgbwColor((byte)Math.Round(R * factor), (byte)Math.Round(G * factor), (byte)Math.Round(B * factor), (byte)Math.Round(W * factor));
    }

    /// <summary>Converts to device levels (0..100 per channel) at the given brightness (0..1).</summary>
    public LedLevels ToLevels(double brightness = 1)
    {
        brightness = Math.Clamp(brightness, 0, 1);
        return new LedLevels(Level(R), Level(G), Level(B), Level(W));

        byte Level(byte c) => (byte)Math.Clamp(Math.Round(c / 255.0 * 100 * brightness), 0, 100);
    }

    public static RgbwColor FromHsv(double hueDegrees, double saturation = 1, double value = 1)
    {
        var h = ((hueDegrees % 360) + 360) % 360 / 60;
        var c = value * saturation;
        var x = c * (1 - Math.Abs((h % 2) - 1));
        var m = value - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new RgbwColor((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>Device backlight levels, 0..100 per channel, as sent to the hub.</summary>
public readonly record struct LedLevels(byte R, byte G, byte B, byte W)
{
    public static readonly LedLevels Off = new(0, 0, 0, 0);

    [JsonIgnore]
    public bool IsOff => R == 0 && G == 0 && B == 0 && W == 0;

    /// <summary>Largest per-channel difference, used to skip imperceptible updates.</summary>
    public int Distance(LedLevels other) =>
        Math.Max(Math.Max(Math.Abs(R - other.R), Math.Abs(G - other.G)), Math.Max(Math.Abs(B - other.B), Math.Abs(W - other.W)));

    public LedLevels Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new LedLevels(S(R), S(G), S(B), S(W));

        byte S(byte v) => (byte)Math.Round(v * factor);
    }

    /// <summary>Approximate display color for previews (white LED adds equally to all channels).</summary>
    public RgbwColor ToPreviewColor()
    {
        var white = W * 0.8;
        return new RgbwColor(C(R, white), C(G, white), C(B, white));

        static byte C(byte v, double white) => (byte)Math.Clamp(Math.Round((v + white) / 100.0 * 255), 0, 255);
    }
}

public sealed class RgbwColorJsonConverter : JsonConverter<RgbwColor>
{
    public override RgbwColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && RgbwColor.TryParse(reader.GetString(), out var color))
        {
            return color;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            byte r = 0, g = 0, b = 0, w = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                var v = (byte)Math.Clamp(reader.GetInt32(), 0, 255);
                switch (name?.ToLowerInvariant())
                {
                    case "r": r = v; break;
                    case "g": g = v; break;
                    case "b": b = v; break;
                    case "w": w = v; break;
                }
            }

            return new RgbwColor(r, g, b, w);
        }

        throw new JsonException("Invalid color value.");
    }

    public override void Write(Utf8JsonWriter writer, RgbwColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}
