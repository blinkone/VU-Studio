using VuStudio.Core.Faces;

namespace VuStudio.Rendering.Drawing;

/// <summary>Draws <see cref="IconLayer"/>s: a Material Design icon scaled to Size and centred on (X, Y).</summary>
internal static class IconDrawer
{
    public static void Draw(FaceCanvas face, IconLayer layer)
    {
        var id = TextTemplate.Render(layer.Icon, face.Context).Trim();
        var icons = face.Assets.Icons;
        var path = icons.GetPath(id);
        if (path is null)
        {
            return;
        }

        var size = Num.Clamp(layer.Size, 1, 400, 24);
        var scale = size / icons.ViewBox;

        // Whole-pixel origin keeps the icons' 24-unit grid on the pixel grid (crisp at 24, 48…).
        var left = Num.Snap(Num.F(layer.X) - (size / 2));
        var top = Num.Snap(Num.F(layer.Y) - (size / 2));
        var canvas = face.Canvas;
        canvas.Save();
        canvas.Translate(left, top);
        canvas.Scale(scale);
        using var paint = Paints.Fill(face.Color(layer.Ink));
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }
}
