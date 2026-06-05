using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.UI;
using ImprimePlus.Core.Layout;

namespace ImprimePlus.Editor;

/// <summary>Cómo encaja la imagen en su celda.</summary>
public enum FitMode { Cover, Contain, Stretch }

/// <summary>Forma de recorte de la celda.</summary>
public enum ImageShape { Rectangle, Rounded, Circle, Hexagon, Star }

/// <summary>Posición del título/caption respecto a la imagen.</summary>
public enum CaptionPosition { None, Below, Above, Overlay }

/// <summary>Intensidad de la sombra de la imagen.</summary>
public enum ShadowStrength { None, Soft, Medium, Strong }

/// <summary>
/// Una imagen en el editor: bitmap GPU + ajustes. Las props de forma/borde/ajuste
/// son NULLABLE = "usar global" (se resuelven contra <see cref="GlobalDefaults"/>).
/// </summary>
public sealed class EditorImage
{
    public string Id { get; }
    public string? SourcePath { get; set; }
    public byte[]? SourceBytes { get; set; }
    public CanvasBitmap Bitmap { get; set; }

    // Backup para "Restaurar original" (tras quitar fondo).
    public CanvasBitmap? OriginalBitmap { get; set; }
    public byte[]? OriginalBytes { get; set; }
    public string? OriginalPath { get; set; }

    public ImageOverrides Overrides { get; } = new();   // colSpan/rowSpan

    // Encaje (Fit es nullable = usar global).
    public FitMode? Fit { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double OffsetX { get; set; } = 0;
    public double OffsetY { get; set; } = 0;
    public double RotationDeg { get; set; } = 0;
    public double? Width { get; set; }   // ancho per-imagen (modo tamaño)
    public double? Height { get; set; }

    // Forma y borde (nullable = usar global).
    public ImageShape? Shape { get; set; }
    public double? CornerRadius { get; set; }
    public double? BorderWidth { get; set; }
    public Color? BorderColor { get; set; }
    public Color? BackgroundColor { get; set; }
    public ShadowStrength? Shadow { get; set; }

    // Filtros (1.0 = sin cambio; grayscale/sepia/invert 0..1; hue 0..360; blur 0..10; opacity 0..1).
    public double Brightness { get; set; } = 1.0;
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public double Grayscale { get; set; } = 0.0;
    public double Sepia { get; set; } = 0.0;
    public double Hue { get; set; } = 0.0;
    public double Blur { get; set; } = 0.0;
    public double Invert { get; set; } = 0.0;
    public double Opacity { get; set; } = 1.0;

    // Caption.
    public string CaptionText { get; set; } = "";
    public CaptionPosition CaptionPos { get; set; } = CaptionPosition.None;
    public string CaptionFont { get; set; } = "Segoe UI";
    public double CaptionSize { get; set; } = 14;
    public Color CaptionColor { get; set; } = Colors.White;
    public Color CaptionBg { get; set; } = Color.FromArgb(140, 0, 0, 0);

    public EditorImage(string id, CanvasBitmap bitmap)
    {
        Id = id;
        Bitmap = bitmap;
    }

    // ----- valores efectivos (override per-imagen ?? global) -----
    public FitMode EffFit(GlobalDefaults g) => Fit ?? g.Fit;
    public ImageShape EffShape(GlobalDefaults g) => Shape ?? g.Shape;
    public double EffCornerRadius(GlobalDefaults g) => CornerRadius ?? g.CornerRadius;
    public double EffBorderWidth(GlobalDefaults g) => BorderWidth ?? g.BorderWidth;
    public Color EffBorderColor(GlobalDefaults g) => BorderColor ?? g.BorderColor;
    public Color EffBgColor(GlobalDefaults g) => BackgroundColor ?? g.CellBg;
    public ShadowStrength EffShadow(GlobalDefaults g) => Shadow ?? g.Shadow;

    public bool HasFilters =>
        Brightness != 1.0 || Contrast != 1.0 || Saturation != 1.0 || Grayscale > 0 ||
        Sepia > 0 || Hue != 0 || Blur > 0 || Invert > 0 || Opacity < 1.0;

    public ImageItem ToItem() => new() { Id = Id, SourcePath = SourcePath, Overrides = Overrides };
}
