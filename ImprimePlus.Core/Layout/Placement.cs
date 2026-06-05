namespace ImprimePlus.Core.Layout;

/// <summary>Posición final de una imagen dentro de la cuadrícula de su página.</summary>
public readonly record struct Placement(ImageItem Image, int Col, int Row, int ColSpan, int RowSpan);

public static partial class LayoutEngine
{
    /// <summary>
    /// Coloca las imágenes de UNA página en la cuadrícula (cols×rows) con el mismo
    /// algoritmo first-fit row-major que usa <see cref="Paginate"/> para decidir
    /// el corte de página — así la posición de cada celda coincide con la página
    /// que la paginación le asignó. Equivale al auto-flow de CSS grid del editor viejo.
    /// </summary>
    public static List<Placement> PlacePage(IReadOnlyList<ImageItem> images, int cols, int rows)
    {
        var result = new List<Placement>(images.Count);
        var grid = MakeGrid(cols, rows);

        foreach (var img in images)
        {
            if (img is null) continue;
            var ov = img.Overrides ?? new ImageOverrides();
            int cs = Math.Min(ov.ColSpan <= 0 ? 1 : ov.ColSpan, cols);
            int rs = Math.Min(ov.RowSpan <= 0 ? 1 : ov.RowSpan, rows);

            if (TryPlaceAt(grid, cols, rows, cs, rs, out int col, out int row))
                result.Add(new Placement(img, col, row, cs, rs));
            // (si no cabe, esa imagen pertenecía a otra página; PlacePage se llama
            //  con las imágenes ya paginadas, así que en la práctica siempre cabe)
        }
        return result;
    }

    /// <summary>Como TryPlace pero devuelve la celda (col,row) donde colocó.</summary>
    private static bool TryPlaceAt(bool[][] grid, int cols, int rows, int cs, int rs, out int col, out int row)
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
                    col = c; row = r;
                    return true;
                }
            }
        }
        col = -1; row = -1;
        return false;
    }
}
