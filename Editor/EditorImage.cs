using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.UI;
using ImprimePlus.Core.Layout;

namespace ImprimePlus.Editor;

/// <summary>Cómo encaja la imagen en su celda.</summary>
public enum FitMode
{
    Cover,   // llena la celda recortando (default)
    Contain, // entra completa con letterbox
    Stretch, // deforma para llenar
}

/// <summary>Forma de recorte de la celda.</summary>
public enum ImageShape
{
    Rectangle,
    Rounded,
    Circle,
    Hexagon,
    Star,
}

/// <summary>Posición del título/caption respecto a la imagen.</summary>
public enum CaptionPosition
{
    None,
    Below,
    Above,
    Overlay,
}

/// <summary>Intensidad de la sombra de la imagen.</summary>
public enum ShadowStrength
{
    None,
    Soft,
    Medium,
    Strong,
}

/// <summary>
/// Una imagen en el editor: el bitmap cargado (GPU) + todos los ajustes de
/// presentación (fit, forma, borde, sombra, filtros, caption). Los overrides de
/// cuadrícula (colSpan/rowSpan) viven en <see cref="Overrides"/>, que es lo que
/// consume el LayoutEngine para paginar/colocar.
/// </summary>
public sealed class EditorImage
{
    public string Id { get; }
    public string? SourcePath { get; init; }
    public byte[]? SourceBytes { get; init; }   // origen sin archivo (portapapeles, comprimido)
    public CanvasBitmap Bitmap { get; set; }

    // Overrides de layout (spans) — los lee el motor.
    public ImageOverrides Overrides { get; } = new();

    // Encaje dentro de la celda.
    public FitMode Fit { get; set; } = FitMode.Cover;
    public double Zoom { get; set; } = 1.0;     // zoom interno
    public double OffsetX { get; set; } = 0;     // pan interno (-1..1 relativo)
    public double OffsetY { get; set; } = 0;
    public double RotationDeg { get; set; } = 0; // múltiplos de 90 por ahora

    // Forma y borde.
    public ImageShape Shape { get; set; } = ImageShape.Rectangle;
    public double CornerRadius { get; set; } = 14;        // px (forma Rounded)
    public double BorderWidth { get; set; } = 0;          // px
    public Color BorderColor { get; set; } = Colors.White;
    public Color BackgroundColor { get; set; } = Colors.Transparent;
    public ShadowStrength Shadow { get; set; } = ShadowStrength.None;

    // Filtros (1.0 = sin cambio; grayscale/sepia 0..1).
    public double Brightness { get; set; } = 1.0;
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public double Grayscale { get; set; } = 0.0;
    public double Sepia { get; set; } = 0.0;

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

    public bool HasFilters =>
        Brightness != 1.0 || Contrast != 1.0 || Saturation != 1.0 || Grayscale > 0 || Sepia > 0;

    /// <summary>Proyección al modelo de dominio que entiende el LayoutEngine.</summary>
    public ImageItem ToItem() => new() { Id = Id, SourcePath = SourcePath, Overrides = Overrides };
}
