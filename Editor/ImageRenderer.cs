using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace ImprimePlus.Editor;

/// <summary>
/// Dibuja UNA imagen del editor en su celda: forma (recorte por geometría),
/// sombra, fondo, imagen con filtros GPU (CanvasEffects) + fit/zoom/rotación,
/// borde y caption. La misma rutina se usará para imprimir (vectorial).
/// </summary>
public static class ImageRenderer
{
    public static void Draw(CanvasDrawingSession ds, EditorImage img, Rect cell, double scale)
    {
        // 1) Reservar banda para caption arriba/abajo.
        Rect imageRect = cell;
        Rect? captionRect = null;
        bool hasCaption = img.CaptionPos != CaptionPosition.None && !string.IsNullOrWhiteSpace(img.CaptionText);
        double band = hasCaption && (img.CaptionPos is CaptionPosition.Below or CaptionPosition.Above)
            ? img.CaptionSize * scale * 1.6 + 6 : 0;

        if (band > 0)
        {
            if (img.CaptionPos == CaptionPosition.Below)
            {
                imageRect = new Rect(cell.X, cell.Y, cell.Width, cell.Height - band);
                captionRect = new Rect(cell.X, cell.Y + cell.Height - band, cell.Width, band);
            }
            else // Above
            {
                imageRect = new Rect(cell.X, cell.Y + band, cell.Width, cell.Height - band);
                captionRect = new Rect(cell.X, cell.Y, cell.Width, band);
            }
        }
        else if (hasCaption && img.CaptionPos == CaptionPosition.Overlay)
        {
            double oh = img.CaptionSize * scale * 1.6 + 6;
            captionRect = new Rect(cell.X, cell.Y + cell.Height - oh, cell.Width, oh);
        }

        if (imageRect.Width <= 0 || imageRect.Height <= 0) return;

        double radius = img.CornerRadius * scale;
        using var geo = BuildGeometry(ds, img.Shape, imageRect, radius);

        // 2) Sombra (geometría desplazada, translúcida).
        if (img.Shadow != ShadowStrength.None)
        {
            var (a, dx, dy) = img.Shadow switch
            {
                ShadowStrength.Soft => ((byte)40, 2.0, 3.0),
                ShadowStrength.Medium => ((byte)70, 4.0, 6.0),
                _ => ((byte)110, 6.0, 9.0),
            };
            var soff = new Rect(imageRect.X + dx * scale, imageRect.Y + dy * scale, imageRect.Width, imageRect.Height);
            using var sgeo = BuildGeometry(ds, img.Shape, soff, radius);
            ds.FillGeometry(sgeo, Color.FromArgb(a, 0, 0, 0));
        }

        // 3) Fondo de celda.
        if (img.BackgroundColor.A > 0) ds.FillGeometry(geo, img.BackgroundColor);

        // 4) Imagen (con filtros GPU) recortada a la forma.
        var (image, disposables) = BuildFiltered(img);
        try
        {
            using (ds.CreateLayer(1f, geo))
            {
                var prev = ds.Transform;
                ds.Transform = ComputeTransform(img, imageRect);
                ds.DrawImage(image, 0f, 0f);
                ds.Transform = prev;
            }
        }
        finally
        {
            foreach (var d in disposables) d.Dispose();
        }

        // 5) Borde a lo largo de la forma.
        if (img.BorderWidth > 0)
            ds.DrawGeometry(geo, img.BorderColor, (float)(img.BorderWidth * scale));

        // 6) Caption.
        if (captionRect is Rect cr && hasCaption)
        {
            if (img.CaptionBg.A > 0) ds.FillRectangle(cr, img.CaptionBg);
            using var fmt = new CanvasTextFormat
            {
                FontFamily = img.CaptionFont,
                FontSize = (float)Math.Max(6, img.CaptionSize * scale),
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            ds.DrawText(img.CaptionText, cr, img.CaptionColor, fmt);
        }
    }

    // ---------- Geometría de forma ----------

    private static CanvasGeometry BuildGeometry(ICanvasResourceCreator rc, ImageShape shape, Rect r, double radius)
    {
        switch (shape)
        {
            case ImageShape.Rounded:
                double rad = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
                return CanvasGeometry.CreateRoundedRectangle(rc, r, (float)rad, (float)rad);
            case ImageShape.Circle:
                return CanvasGeometry.CreateEllipse(rc,
                    (float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2),
                    (float)(r.Width / 2), (float)(r.Height / 2));
            case ImageShape.Hexagon:
                return CanvasGeometry.CreatePolygon(rc, RegularPolygon(r, 6, -90));
            case ImageShape.Star:
                return CanvasGeometry.CreatePolygon(rc, Star(r, 5, 0.42));
            default:
                return CanvasGeometry.CreateRectangle(rc, r);
        }
    }

    private static Vector2[] RegularPolygon(Rect r, int sides, double startDeg)
    {
        float cx = (float)(r.X + r.Width / 2), cy = (float)(r.Y + r.Height / 2);
        float rx = (float)(r.Width / 2), ry = (float)(r.Height / 2);
        var pts = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            double a = (startDeg + i * 360.0 / sides) * Math.PI / 180.0;
            pts[i] = new Vector2(cx + rx * (float)Math.Cos(a), cy + ry * (float)Math.Sin(a));
        }
        return pts;
    }

    private static Vector2[] Star(Rect r, int points, double innerRatio)
    {
        float cx = (float)(r.X + r.Width / 2), cy = (float)(r.Y + r.Height / 2);
        float rx = (float)(r.Width / 2), ry = (float)(r.Height / 2);
        var pts = new Vector2[points * 2];
        for (int i = 0; i < points * 2; i++)
        {
            double a = (-90 + i * 180.0 / points) * Math.PI / 180.0;
            double f = (i % 2 == 0) ? 1.0 : innerRatio;
            pts[i] = new Vector2(cx + rx * (float)(f * Math.Cos(a)), cy + ry * (float)(f * Math.Sin(a)));
        }
        return pts;
    }

    // ---------- Filtros (cadena de CanvasEffects en GPU) ----------

    private static (ICanvasImage image, List<IDisposable> disposables) BuildFiltered(EditorImage img)
    {
        ICanvasImage cur = img.Bitmap;
        var disp = new List<IDisposable>();
        if (!img.HasFilters) return (cur, disp);

        if (img.Brightness != 1.0)
        {
            var e = new ExposureEffect { Source = cur, Exposure = (float)Math.Log2(Math.Max(0.01, img.Brightness)) };
            disp.Add(e); cur = e;
        }
        if (img.Contrast != 1.0)
        {
            var e = new ContrastEffect { Source = cur, Contrast = (float)Math.Clamp(img.Contrast - 1.0, -1.0, 1.0) };
            disp.Add(e); cur = e;
        }
        // Saturación + grayscale combinados: el grayscale es desaturación (CSS-like).
        double satUI = img.Saturation * (1.0 - Math.Clamp(img.Grayscale, 0.0, 1.0));
        if (Math.Abs(satUI - 1.0) > 1e-6 || img.Grayscale > 0)
        {
            var e = new SaturationEffect { Source = cur, Saturation = (float)Math.Clamp(satUI / 2.0, 0.0, 1.0) };
            disp.Add(e); cur = e;
        }
        if (img.Sepia > 0)
        {
            var e = new SepiaEffect { Source = cur, Intensity = (float)Math.Clamp(img.Sepia, 0.0, 1.0) };
            disp.Add(e); cur = e;
        }
        return (cur, disp);
    }

    // ---------- Transform de fit/zoom/rotación ----------

    private static Matrix3x2 ComputeTransform(EditorImage img, Rect rect)
    {
        var bmp = img.Bitmap;
        float bw = (float)bmp.Size.Width, bh = (float)bmp.Size.Height;
        bool quarter = ((int)Math.Round(img.RotationDeg / 90.0)) % 2 != 0;
        float ebw = quarter ? bh : bw;
        float ebh = quarter ? bw : bh;
        var center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
        var offset = new Vector2((float)(img.OffsetX * rect.Width), (float)(img.OffsetY * rect.Height));
        float rot = (float)(img.RotationDeg * Math.PI / 180.0);

        if (img.Fit == FitMode.Stretch)
        {
            float sx = (float)(rect.Width / ebw), sy = (float)(rect.Height / ebh);
            return Matrix3x2.CreateTranslation(-bw / 2f, -bh / 2f)
                 * Matrix3x2.CreateScale(quarter ? sy : sx, quarter ? sx : sy)
                 * Matrix3x2.CreateRotation(rot)
                 * Matrix3x2.CreateTranslation(center + offset);
        }

        double sxu = rect.Width / ebw, syu = rect.Height / ebh;
        double s = (img.Fit == FitMode.Contain ? Math.Min(sxu, syu) : Math.Max(sxu, syu)) * img.Zoom;
        return Matrix3x2.CreateTranslation(-bw / 2f, -bh / 2f)
             * Matrix3x2.CreateScale((float)s)
             * Matrix3x2.CreateRotation(rot)
             * Matrix3x2.CreateTranslation(center + offset);
    }
}
