using Microsoft.UI;
using Windows.UI;

namespace ImprimePlus.Editor;

/// <summary>Alineación horizontal del contenido en la celda (modo Contener).</summary>
public enum AlignH { Left, Center, Right, Justify }

/// <summary>Alineación vertical del contenido en la celda (modo Contener).</summary>
public enum AlignV { Top, Center, Bottom }

/// <summary>
/// Defaults globales aplicados a las imágenes que no tienen override propio
/// (las props *Ov de EditorImage en null = "usar global"). Espejo de la sección
/// "Imágenes (global)" del Imprime+ viejo.
/// </summary>
public sealed class GlobalDefaults
{
    public ImageShape Shape { get; set; } = ImageShape.Rectangle;
    public double BorderWidth { get; set; } = 0;
    public Color BorderColor { get; set; } = Colors.Black;
    public double CornerRadius { get; set; } = 0;
    public ShadowStrength Shadow { get; set; } = ShadowStrength.None;
    public FitMode Fit { get; set; } = FitMode.Cover;
    public Color CellBg { get; set; } = Colors.Transparent;
    public AlignH AlignH { get; set; } = AlignH.Center;
    public AlignV AlignV { get; set; } = AlignV.Top;

    public bool CutGuides { get; set; } = false; // toggle "Guías de corte" (G5)

    // --- Títulos (global) ---  se aplican a las imágenes SIN título individual.
    public bool CaptionsOn { get; set; } = false;
    public CaptionSource CaptionSource { get; set; } = CaptionSource.Filename;
    public bool CaptionFilenameExt { get; set; } = false; // false = sin extensión (default)
    public CaptionPosition CaptionPos { get; set; } = CaptionPosition.Below;
    public string CaptionFont { get; set; } = "Segoe UI";
    public double CaptionSize { get; set; } = 14;
    public Color CaptionColor { get; set; } = Colors.Black;
    public Color CaptionBg { get; set; } = Colors.Transparent;
}
