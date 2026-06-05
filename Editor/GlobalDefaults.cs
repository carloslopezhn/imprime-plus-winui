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
}
