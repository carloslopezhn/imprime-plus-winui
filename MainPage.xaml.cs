using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using ImprimePlus.Core.Layout;
using ImprimePlus.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ImprimePlus;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    // Configuración por defecto (Carta 3x3). En Fase 4 la maneja el inspector
    // y se redibuja con PageCanvas.Invalidate() en cada cambio.
    private readonly LayoutConfig _config = new()
    {
        Unit = Units.Cm,
        PageWidth = 21.59,
        PageHeight = 27.94,
        SpacingH = 0.3,
        SpacingV = 0.3,
        LayoutMode = LayoutModes.Grid,
        GridRows = 3,
        GridCols = 3,
    };

    public MainPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Render del editor (Fase 3, primer incremento): dibuja la hoja y la
    /// cuadrícula de celdas vacías que calcula el LayoutEngine compartido.
    /// 1 px de layout (96 DPI) == 1 DIP de Win2D, así que sólo aplicamos
    /// un factor de zoom-a-ventana y centramos.
    /// </summary>
    private void PageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var size = sender.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var layout = LayoutEngine.ComputeLayout(_config);
        if (layout.PageW <= 0 || layout.PageH <= 0) return;

        const double pad = 32;
        double scale = Math.Min(
            (size.Width - 2 * pad) / layout.PageW,
            (size.Height - 2 * pad) / layout.PageH);
        if (scale <= 0 || double.IsNaN(scale)) scale = 0.1;

        double pageW = layout.PageW * scale;
        double pageH = layout.PageH * scale;
        double ox = (size.Width - pageW) / 2.0;
        double oy = (size.Height - pageH) / 2.0;

        // Sombra sutil de la hoja.
        ds.FillRectangle(
            new Rect(ox + 3, oy + 4, pageW, pageH),
            ColorHelper.FromArgb(40, 0, 0, 0));

        // Hoja (blanca con borde).
        var pageRect = new Rect(ox, oy, pageW, pageH);
        ds.FillRectangle(pageRect, Colors.White);
        ds.DrawRectangle(pageRect, ColorHelper.FromArgb(255, 0xC7, 0xD2, 0xE0), 1f);

        // Celdas vacías (estilo guía de corte: borde punteado azul + relleno tenue).
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        Color cellFill = ColorHelper.FromArgb(255, 0xF1, 0xF5, 0xFB);
        Color cellStroke = ColorHelper.FromArgb(150, 0x3B, 0x82, 0xF6);

        for (int row = 0; row < layout.Rows; row++)
        {
            for (int col = 0; col < layout.Cols; col++)
            {
                double cx = layout.MarginLeft + col * (layout.CellW + layout.SpacingH);
                double cy = layout.MarginTop + row * (layout.CellH + layout.SpacingV);
                var cell = new Rect(
                    ox + cx * scale,
                    oy + cy * scale,
                    layout.CellW * scale,
                    layout.CellH * scale);
                ds.FillRectangle(cell, cellFill);
                ds.DrawRectangle(cell, cellStroke, 1f, dashed);
            }
        }
    }
}
