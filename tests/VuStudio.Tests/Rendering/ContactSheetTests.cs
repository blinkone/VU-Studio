using SkiaSharp;
using VuStudio.Core.Faces;
using VuStudio.Core.Lighting;
using VuStudio.Rendering;

namespace VuStudio.Tests.Rendering;

/// <summary>
/// Builds the README contact sheet (every built-in theme at 2×, tinted like a lit backlight).
/// Regenerate <c>docs/images/themes.png</c> with:
/// <c>VUSTUDIO_CONTACT_SHEET=docs/images/themes.png dotnet test --project tests/VuStudio.Tests -- --filter-class "*ContactSheetTests"</c>
/// </summary>
public class ContactSheetTests
{
    private const int Scale = 2;
    private const int Columns = 2;
    private const int Gap = 20;
    private const int LabelHeight = 30;

    private static readonly (string Theme, FaceRenderContext Context, FaceConfig Config, RgbwColor Tint)[] Tiles =
    [
        ("vintage/classic", RenderingTestData.Context("CPU", 63.4, "%"), new FaceConfig(), new RgbwColor(255, 196, 120)),
        ("vintage/vu", RenderingTestData.Context("Audio L", 58, "", icon: null), new FaceConfig { ShowValue = false }, new RgbwColor(255, 214, 150)),
        ("minimal/arc", RenderingTestData.Context("GPU Hot Spot", 87.5, "°C", 0, 110, "thermometer"), new FaceConfig(), RgbwColor.White),
        ("minimal/number", RenderingTestData.Context("Download", 12345, "Mbit/s", 0, 20000, "download", decimals: 0), new FaceConfig { WarnFrom = null }, new RgbwColor(170, 230, 255)),
        ("minimal/glance", RenderingTestData.Context("Memory", 41.8, "%", icon: "memory"), new FaceConfig(), new RgbwColor(200, 255, 200)),
        ("instrument/gauge", RenderingTestData.Context("Pump", 1840, "RPM", 0, 3000, "fan", decimals: 0), new FaceConfig { WarnFrom = 0.8 }, new RgbwColor(255, 170, 150)),
        ("instrument/aviation", RenderingTestData.Context("EGT", 712, "°C", 0, 900, null, decimals: 0), new FaceConfig { WarnFrom = 0.85 }, new RgbwColor(180, 255, 210)),
        ("hud/frame", RenderingTestData.Context("Ping", 7, "ms", 0, 100, "lan", decimals: 0), new FaceConfig(), new RgbwColor(150, 220, 255)),
        ("hud/grid", RenderingTestData.Context("CPU Load", 63.4, "%"), new FaceConfig { ShowSparkline = true }, new RgbwColor(190, 255, 190)),
        ("retro/pixel", RenderingTestData.Context("FPS", 144, "fps", 0, 240, "controller", decimals: 0), new FaceConfig { ShowSparkline = true, Inverted = true }, new RgbwColor(230, 200, 255)),
    ];

    [Fact]
    public void Contact_sheet_shows_every_theme_and_stays_small()
    {
        var renderer = new FaceRenderer();
        Assert.Equal(renderer.Themes.Select(t => t.Id).Order(), Tiles.Select(t => t.Theme).Order());

        var png = Build(renderer);
        Assert.True(png.Length < 400 * 1024, $"{png.Length / 1024} KB");

        var target = Environment.GetEnvironmentVariable("VUSTUDIO_CONTACT_SHEET");
        if (!string.IsNullOrWhiteSpace(target))
        {
            var path = Path.IsPathRooted(target) ? target : Path.Combine(RepositoryRoot(), target);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
        }
    }

    private static byte[] Build(FaceRenderer renderer)
    {
        const int tileWidth = MonoBitmap.DialWidth * Scale;
        const int tileHeight = MonoBitmap.DialHeight * Scale;
        var rows = (Tiles.Length + Columns - 1) / Columns;
        var width = (Columns * tileWidth) + ((Columns + 1) * Gap);
        var height = (rows * (tileHeight + LabelHeight)) + ((rows + 1) * Gap);

        using var sheet = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(0x1E, 0x1F, 0x24));
        var fonts = FaceAssets.Shared.Fonts;
        using var idFont = new SKFont(fonts.Resolve("inter", 600, false, 13).Typeface, 13) { Edging = SKFontEdging.Antialias };
        using var nameFont = new SKFont(fonts.Resolve("inter", 400, false, 13).Typeface, 13) { Edging = SKFontEdging.Antialias };
        using var idPaint = new SKPaint { Color = new SKColor(0xEE, 0xEE, 0xF0), IsAntialias = true };
        using var namePaint = new SKPaint { Color = new SKColor(0x9A, 0x9C, 0xA6), IsAntialias = true };
        using var bezel = new SKPaint { Color = new SKColor(0x0B, 0x0B, 0x0D), IsAntialias = true };

        for (var i = 0; i < Tiles.Length; i++)
        {
            var (themeId, context, config, tint) = Tiles[i];
            config.Theme = themeId;
            var bitmap = renderer.Render(renderer.BuildDocument(config, context), context);
            using var face = SKImage.FromEncodedData(renderer.EncodePng(bitmap, Scale, tint));
            var x = Gap + ((i % Columns) * (tileWidth + Gap));
            var y = Gap + ((i / Columns) * (tileHeight + LabelHeight + Gap));
            canvas.DrawRoundRect(new SKRect(x - 4, y - 4, x + tileWidth + 4, y + tileHeight + 4), 6, 6, bezel);
            canvas.DrawImage(face, x, y, new SKSamplingOptions(SKFilterMode.Nearest), null);
            var info = renderer.Themes.Single(t => t.Id == themeId);
            var baseline = y + tileHeight + 22;
            canvas.DrawText(themeId, x, baseline, SKTextAlign.Left, idFont, idPaint);
            canvas.DrawText(info.Name.En, x + tileWidth, baseline, SKTextAlign.Right, nameFont, namePaint);
        }

        using var data = sheet.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VuStudio.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
