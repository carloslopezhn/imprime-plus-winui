namespace ImprimePlus.Core.Layout;

/// <summary>
/// Motor de layout — port 1:1 de <c>src/js/engine.js</c> del Imprime+ Tauri.
/// Calcula la cuadrícula, el tamaño de celda y la paginación (con colSpan/rowSpan).
/// Es matemática pura, sin dependencias de UI: lo usa tanto el editor (Win2D)
/// como la impresión (CanvasPrintDocument), garantizando WYSIWYG.
/// </summary>
public static class LayoutEngine
{
    // Conversión de cualquier unidad a px a 96 DPI (idéntico a engine.js).
    private static double UnitToPx(string unit) => unit switch
    {
        Units.In => 96.0,
        Units.Mm => 96.0 / 25.4,
        _ => 96.0 / 2.54, // cm (default)
    };

    /// <summary>value (en <paramref name="unit"/>) → px @96dpi.</summary>
    public static double ToPx(double value, string unit) => value * UnitToPx(unit);

    /// <summary>px @96dpi → value en <paramref name="unit"/>.</summary>
    public static double FromPx(double px, string unit) => px / UnitToPx(unit);

    /// <summary>
    /// Calcula la cuadrícula para una configuración. Devuelve medidas en px @96dpi.
    /// Port exacto de <c>computeLayout</c>.
    /// </summary>
    public static LayoutResult ComputeLayout(LayoutConfig config)
    {
        var unit = string.IsNullOrEmpty(config.Unit) ? Units.Cm : config.Unit;
        double pageW = ToPx(config.PageWidth, unit);
        double pageH = ToPx(config.PageHeight, unit);
        double mt = ToPx(config.MarginTop, unit);
        double mr = ToPx(config.MarginRight, unit);
        double mb = ToPx(config.MarginBottom, unit);
        double ml = ToPx(config.MarginLeft, unit);
        double sh = ToPx(config.SpacingH, unit);
        double sv = ToPx(config.SpacingV, unit);

        double contentW = pageW - ml - mr;
        double contentH = pageH - mt - mb;

        int cols, rows;
        double cellW, cellH;

        switch (config.LayoutMode)
        {
            case LayoutModes.Grid:
                cols = Math.Max(1, config.GridCols);
                rows = Math.Max(1, config.GridRows);
                cellW = (contentW - sh * (cols - 1)) / cols;
                cellH = (contentH - sv * (rows - 1)) / rows;
                break;

            case LayoutModes.Count:
            {
                int count = Math.Max(1, config.CountPerPage);
                // Mejor cols/rows que llene la página siendo lo más cuadrado posible.
                double ratio = contentW / contentH;
                cols = Math.Max(1, (int)Math.Round(Math.Sqrt(count * ratio), MidpointRounding.AwayFromZero));
                rows = (int)Math.Ceiling((double)count / cols);
                while (cols * rows < count) rows++; // ajuste si nos quedamos cortos
                cellW = (contentW - sh * (cols - 1)) / cols;
                cellH = (contentH - sv * (rows - 1)) / rows;
                break;
            }

            case LayoutModes.Size:
                cellW = ToPx(config.ImgWidth, unit);
                cellH = ToPx(config.ImgHeight, unit);
                cols = Math.Max(1, (int)Math.Floor((contentW + sh) / (cellW + sh)));
                rows = Math.Max(1, (int)Math.Floor((contentH + sv) / (cellH + sv)));
                break;

            default:
                cols = 3; rows = 3;
                cellW = (contentW - sh * 2) / 3;
                cellH = (contentH - sv * 2) / 3;
                break;
        }

        return new LayoutResult
        {
            PageW = pageW,
            PageH = pageH,
            ContentW = contentW,
            ContentH = contentH,
            MarginTop = mt,
            MarginRight = mr,
            MarginBottom = mb,
            MarginLeft = ml,
            SpacingH = sh,
            SpacingV = sv,
            Cols = cols,
            Rows = rows,
            CellW = cellW,
            CellH = cellH,
            TotalSlots = cols * rows,
            PerPage = config.LayoutMode == LayoutModes.Count ? Math.Max(1, config.CountPerPage) : cols * rows,
            Unit = unit,
        };
    }

    /// <summary>Intenta colocar un span (cs×rs) en la cuadrícula. Marca y devuelve true si cupo.</summary>
    private static bool TryPlace(bool[][] grid, int cols, int rows, int cs, int rs)
    {
        for (int r = 0; r <= rows - rs; r++)
        {
            for (int c = 0; c <= cols - cs; c++)
            {
                bool fits = true;
                for (int dr = 0; dr < rs && fits; dr++)
                    for (int dc = 0; dc < cs && fits; dc++)
                        if (grid[r + dr][c + dc]) fits = false;

                if (fits)
                {
                    for (int dr = 0; dr < rs; dr++)
                        for (int dc = 0; dc < cs; dc++)
                            grid[r + dr][c + dc] = true;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool[][] MakeGrid(int cols, int rows)
    {
        var g = new bool[rows][];
        for (int r = 0; r < rows; r++) g[r] = new bool[cols];
        return g;
    }

    /// <summary>
    /// Distribuye las imágenes en páginas, respetando colSpan/rowSpan y el límite
    /// por página del modo "count". Port exacto de <c>paginate</c>.
    /// </summary>
    public static List<PageResult> Paginate(IReadOnlyList<ImageItem?> images, LayoutResult layout)
    {
        var pages = new List<PageResult>();
        int cols = layout.Cols;
        int rows = layout.Rows;
        int perPage = layout.PerPage;
        var grid = MakeGrid(cols, rows);
        var pageImages = new List<ImageItem>();

        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (img is null) continue;
            var ov = img.Overrides ?? new ImageOverrides();
            int cs = Math.Min(ov.ColSpan <= 0 ? 1 : ov.ColSpan, cols);
            int rs = Math.Min(ov.RowSpan <= 0 ? 1 : ov.RowSpan, rows);

            // En modo count, además respetar el límite de imágenes por página.
            bool countFull = (perPage < cols * rows) && (pageImages.Count >= perPage);

            if (countFull || !TryPlace(grid, cols, rows, cs, rs))
            {
                // La página actual no admite esta imagen -> nueva página.
                pages.Add(new PageResult { Images = pageImages });
                pageImages = new List<ImageItem>();
                grid = MakeGrid(cols, rows);
                TryPlace(grid, cols, rows, cs, rs); // recolocar en página fresca
            }

            pageImages.Add(img);
        }

        pages.Add(new PageResult { Images = pageImages });
        if (pages.Count == 0) pages.Add(new PageResult());
        return pages;
    }
}
