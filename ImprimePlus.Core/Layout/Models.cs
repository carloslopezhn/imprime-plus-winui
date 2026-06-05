namespace ImprimePlus.Core.Layout;

/// <summary>Unidades soportadas (paridad con engine.js UNIT_TO_PX).</summary>
public static class Units
{
    public const string Cm = "cm";
    public const string In = "in";
    public const string Mm = "mm";
}

/// <summary>Modos de distribución (paridad con engine.js layoutMode).</summary>
public static class LayoutModes
{
    public const string Grid = "grid";
    public const string Count = "count";
    public const string Size = "size";
}

/// <summary>
/// Configuración global de página + distribución. Mapea 1:1 al objeto
/// <c>config</c> que recibía <c>Engine.computeLayout</c> en engine.js.
/// Todas las medidas (page/margin/spacing/img) van en la unidad <see cref="Unit"/>.
/// </summary>
public sealed class LayoutConfig
{
    public string Unit { get; set; } = Units.Cm;

    public double PageWidth { get; set; }
    public double PageHeight { get; set; }

    public double MarginTop { get; set; }
    public double MarginRight { get; set; }
    public double MarginBottom { get; set; }
    public double MarginLeft { get; set; }

    public double SpacingH { get; set; }
    public double SpacingV { get; set; }

    public string LayoutMode { get; set; } = LayoutModes.Grid;

    public int GridCols { get; set; } = 3;
    public int GridRows { get; set; } = 3;

    public int CountPerPage { get; set; } = 9;

    public double ImgWidth { get; set; }
    public double ImgHeight { get; set; }

    // --- Preferencias de impresión (persistidas en settings.json) ---
    /// <summary>Mostrar la vista previa propia de la app antes de imprimir.</summary>
    public bool ShowPrintPreview { get; set; } = true;
    /// <summary>"printable" = ajustar al área imprimible (no recorta); "actual" = tamaño real 1:1.</summary>
    public string PrintFit { get; set; } = "printable";
}

/// <summary>Overrides por imagen relevantes para paginación (resto en fases posteriores).</summary>
public sealed class ImageOverrides
{
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
}

/// <summary>Una imagen del documento. En Fase 2 sólo importan id + overrides.</summary>
public sealed class ImageItem
{
    public string Id { get; set; } = "";
    public string? SourcePath { get; set; }
    public ImageOverrides Overrides { get; set; } = new();
}

/// <summary>
/// Resultado de <see cref="LayoutEngine.ComputeLayout"/> — todas las medidas
/// en PÍXELES a 96 DPI (igual que engine.js, que renderizaba en CSS px).
/// </summary>
public sealed class LayoutResult
{
    public double PageW { get; init; }
    public double PageH { get; init; }
    public double ContentW { get; init; }
    public double ContentH { get; init; }
    public double MarginTop { get; init; }
    public double MarginRight { get; init; }
    public double MarginBottom { get; init; }
    public double MarginLeft { get; init; }
    public double SpacingH { get; init; }
    public double SpacingV { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public double CellW { get; init; }
    public double CellH { get; init; }
    public int TotalSlots { get; init; }
    public int PerPage { get; init; }
    public string Unit { get; init; } = Units.Cm;
}

/// <summary>Una página tras paginar: las imágenes que le tocan.</summary>
public sealed class PageResult
{
    public List<ImageItem> Images { get; init; } = new();
}
