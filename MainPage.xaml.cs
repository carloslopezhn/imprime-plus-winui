using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using ImprimePlus.Core.Layout;
using ImprimePlus.Editor;

namespace ImprimePlus;

/// <summary>
/// Editor principal. El lienzo central es Win2D (Direct2D/GPU): una sola escena
/// que rinde las páginas y sus imágenes a partir del LayoutEngine compartido.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly List<EditorImage> _images = new();
    private readonly HashSet<EditorImage> _selected = new();

    // Configuración por defecto (Carta 3x3). En Fase 4 la maneja el inspector.
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

    // Estado de vista (zoom de usuario + pan en DIPs de pantalla).
    private double _userZoom = 1.0;
    private double _panX = 0, _panY = 0;

    // Transform de la última pasada de Draw (para hit-test y zoom-al-cursor).
    private double _lastScale = 1, _lastOx, _lastOy, _lastGap = 40;
    private LayoutResult? _lastLayout;
    private List<List<Placement>> _lastPages = new();

    // Pan en curso.
    private bool _panning;
    private Point _lastPointer;

    private int _idSeq;

    private const double PageGap = 40;   // separación entre páginas (unidades world)
    private const double Pad = 32;       // margen de la hoja al viewport

    private bool _ready;

    public MainPage()
    {
        InitializeComponent();
        // Cargar/recargar los bitmaps cuando el device Win2D está listo
        // (y de nuevo si se pierde el device). Es el momento correcto: en Loaded
        // el CanvasControl aún no tiene device.
        PageCanvas.CreateResources += OnCreateResources;
        _ready = true; // a partir de aquí los eventos del inspector ya son del usuario
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        args.TrackAsyncAction(InitOrReloadResourcesAsync(sender).AsAsyncAction());
    }

    private async Task InitOrReloadResourcesAsync(CanvasControl sender)
    {
        if (_images.Count == 0)
        {
            // Primera vez: conveniencia de desarrollo — autocargar ./_sample si existe.
            var dir = Path.Combine(AppContext.BaseDirectory, "_sample");
            if (Directory.Exists(dir))
            {
                var exts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
                var files = Directory.GetFiles(dir)
                    .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f).ToList();
                foreach (var f in files)
                {
                    try
                    {
                        var bmp = await CanvasBitmap.LoadAsync(sender, f);
                        _images.Add(new EditorImage($"img{++_idSeq}", bmp) { SourcePath = f });
                    }
                    catch { }
                }
            }
        }
        else
        {
            // Device-lost: recargar los bitmaps que tengan ruta de origen.
            foreach (var img in _images.Where(i => i.SourcePath is not null))
            {
                try { img.Bitmap = await CanvasBitmap.LoadAsync(sender, img.SourcePath); }
                catch { }
            }
        }
        UpdateChrome();
        sender.Invalidate();

        // Hook de verificación: setear .Text dispara el MISMO TextChanged que teclear,
        // ejercitando campo->_config->reflow de punta a punta.
        if (Environment.GetEnvironmentVariable("IMPRIME_DEV_RELAYOUT") == "1")
        {
            ColsBox.Text = "5";
            RowsBox.Text = "2";
        }
    }

    // ---------- Carga de imágenes ----------

    private async void OnAddImages(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tiff" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            await LoadFiles(files.Select(f => f.Path).ToList());
    }

    private async Task LoadFiles(IReadOnlyList<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            try
            {
                var bmp = await CanvasBitmap.LoadAsync(PageCanvas, path);
                _images.Add(new EditorImage($"img{++_idSeq}", bmp) { SourcePath = path });
                added++;
            }
            catch { /* archivo no decodificable: ignorar por ahora */ }
        }
        if (added > 0)
        {
            UpdateChrome();
            PageCanvas.Invalidate();
        }
    }

    // ---------- Toolbar ----------

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAround(CanvasCenter(), 1.15);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAround(CanvasCenter(), 1 / 1.15);

    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        _userZoom = 1.0; _panX = 0; _panY = 0;
        UpdateChrome();
        PageCanvas.Invalidate();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        _images.Clear();
        _selected.Clear();
        UpdateChrome();
        PageCanvas.Invalidate();
    }

    private Point CanvasCenter() => new(PageCanvas.Size.Width / 2, PageCanvas.Size.Height / 2);

    // ---------- Interacción del lienzo ----------

    private void OnCanvasWheel(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(PageCanvas);
        int delta = p.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAround(p.Position, delta > 0 ? 1.12 : 1 / 1.12);
        e.Handled = true;
    }

    private void ZoomAround(Point cursor, double factor)
    {
        if (_lastLayout is null) { _userZoom *= factor; PageCanvas.Invalidate(); return; }

        double worldW = _lastLayout.PageW;
        double scaleNew = _lastScale * factor;
        double size = PageCanvas.Size.Width;
        double wx = (cursor.X - _lastOx) / _lastScale;
        // mantener el punto bajo el cursor fijo (X e Y)
        _panX = cursor.X - wx * scaleNew - (size - worldW * scaleNew) / 2.0;

        double worldH = _lastPages.Count * _lastLayout.PageH + Math.Max(0, _lastPages.Count - 1) * _lastGap;
        double sizeY = PageCanvas.Size.Height;
        double wy = (cursor.Y - _lastOy) / _lastScale;
        _panY = cursor.Y - wy * scaleNew - (sizeY - worldH * scaleNew) / 2.0;

        _userZoom *= factor;
        UpdateChrome();
        PageCanvas.Invalidate();
    }

    private void OnCanvasPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(PageCanvas);
        _lastPointer = p.Position;
        bool ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);

        if (p.Properties.IsRightButtonPressed)
        {
            var hit = HitTest(p.Position);
            if (hit is not null)
            {
                if (!_selected.Contains(hit)) { _selected.Clear(); _selected.Add(hit); }
                ShowContextMenu(p.Position);
                PageCanvas.Invalidate();
            }
            return;
        }

        if (p.Properties.IsMiddleButtonPressed)
        {
            BeginPan(e);
            return;
        }

        if (p.Properties.IsLeftButtonPressed)
        {
            var hit = HitTest(p.Position);
            if (hit is not null)
            {
                if (ctrl)
                {
                    if (!_selected.Add(hit)) _selected.Remove(hit);
                }
                else
                {
                    _selected.Clear();
                    _selected.Add(hit);
                }
                UpdateChrome();
                PageCanvas.Invalidate();
            }
            else
            {
                if (!ctrl) _selected.Clear();
                BeginPan(e);
                PageCanvas.Invalidate();
            }
        }
    }

    private void BeginPan(PointerRoutedEventArgs e)
    {
        _panning = true;
        PageCanvas.CapturePointer(e.Pointer);
    }

    private void OnCanvasMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var pos = e.GetCurrentPoint(PageCanvas).Position;
        _panX += pos.X - _lastPointer.X;
        _panY += pos.Y - _lastPointer.Y;
        _lastPointer = pos;
        PageCanvas.Invalidate();
    }

    private void OnCanvasReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            PageCanvas.ReleasePointerCapture(e.Pointer);
        }
    }

    // ---------- Menú contextual ----------

    private void ShowContextMenu(Point at)
    {
        var menu = new MenuFlyout();

        var dup = new MenuFlyoutItem { Text = "Duplicar" };
        dup.Click += (_, _) => DuplicateSelected();
        menu.Items.Add(dup);

        var rot = new MenuFlyoutItem { Text = "Rotar 90°" };
        rot.Click += (_, _) => RotateSelected();
        menu.Items.Add(rot);

        menu.Items.Add(new MenuFlyoutSeparator());

        var del = new MenuFlyoutItem { Text = "Eliminar" };
        del.Click += (_, _) => DeleteSelected();
        menu.Items.Add(del);

        menu.ShowAt(PageCanvas, new FlyoutShowOptions { Position = at });
    }

    private void DuplicateSelected()
    {
        var clones = new List<EditorImage>();
        foreach (var img in _images.Where(_selected.Contains).ToList())
        {
            var c = new EditorImage($"img{++_idSeq}", img.Bitmap)
            {
                SourcePath = img.SourcePath,
                Fit = img.Fit,
                Zoom = img.Zoom,
                OffsetX = img.OffsetX,
                OffsetY = img.OffsetY,
                RotationDeg = img.RotationDeg,
            };
            c.Overrides.ColSpan = img.Overrides.ColSpan;
            c.Overrides.RowSpan = img.Overrides.RowSpan;
            int idx = _images.IndexOf(img);
            _images.Insert(idx + 1, c);
            clones.Add(c);
        }
        if (clones.Count > 0) { UpdateChrome(); PageCanvas.Invalidate(); }
    }

    private void RotateSelected()
    {
        foreach (var img in _selected)
            img.RotationDeg = (img.RotationDeg + 90) % 360;
        if (_selected.Count > 0) PageCanvas.Invalidate();
    }

    private void DeleteSelected()
    {
        _images.RemoveAll(_selected.Contains);
        _selected.Clear();
        UpdateChrome();
        PageCanvas.Invalidate();
    }

    // ---------- Hit testing ----------

    private EditorImage? HitTest(Point screen)
    {
        if (_lastLayout is null) return null;
        var L = _lastLayout;
        double wx = (screen.X - _lastOx) / _lastScale;
        double wy = (screen.Y - _lastOy) / _lastScale;

        for (int pi = 0; pi < _lastPages.Count; pi++)
        {
            double pageTop = pi * (L.PageH + _lastGap);
            double localY = wy - pageTop;
            if (localY < 0 || localY > L.PageH) continue;

            foreach (var pl in _lastPages[pi])
            {
                double cellX = L.MarginLeft + pl.Col * (L.CellW + L.SpacingH);
                double cellY = L.MarginTop + pl.Row * (L.CellH + L.SpacingV);
                double spanW = pl.ColSpan * L.CellW + (pl.ColSpan - 1) * L.SpacingH;
                double spanH = pl.RowSpan * L.CellH + (pl.RowSpan - 1) * L.SpacingV;
                if (wx >= cellX && wx <= cellX + spanW && localY >= cellY && localY <= cellY + spanH)
                    return FindById(pl.Image.Id);
            }
        }
        return null;
    }

    private EditorImage? FindById(string id) => _images.FirstOrDefault(i => i.Id == id);

    // ---------- Render ----------

    private void PageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var size = sender.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var layout = LayoutEngine.ComputeLayout(_config);
        if (layout.PageW <= 0 || layout.PageH <= 0) return;

        var items = _images.Select(i => (ImageItem?)i.ToItem()).ToList();
        var pages = LayoutEngine.Paginate(items, layout);
        int nPages = pages.Count;

        double worldW = layout.PageW;
        double worldH = nPages * layout.PageH + (nPages - 1) * PageGap;

        double baseScale = Math.Min(
            (size.Width - 2 * Pad) / worldW,
            (size.Height - 2 * Pad) / worldH);
        if (baseScale <= 0 || double.IsNaN(baseScale) || double.IsInfinity(baseScale)) baseScale = 0.05;

        double scale = baseScale * _userZoom;
        double ox = (size.Width - worldW * scale) / 2.0 + _panX;
        double oy = (size.Height - worldH * scale) / 2.0 + _panY;

        var byId = _images.ToDictionary(i => i.Id);
        var pagePlacements = new List<List<Placement>>(nPages);

        Color border = ColorHelper.FromArgb(255, 0xC7, 0xD2, 0xE0);
        Color cellFill = ColorHelper.FromArgb(255, 0xF1, 0xF5, 0xFB);
        Color cellStroke = ColorHelper.FromArgb(150, 0x3B, 0x82, 0xF6);
        Color selStroke = ColorHelper.FromArgb(255, 0x25, 0x63, 0xEB);
        using var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };

        for (int pi = 0; pi < nPages; pi++)
        {
            var placements = LayoutEngine.PlacePage(pages[pi].Images, layout.Cols, layout.Rows);
            pagePlacements.Add(placements);

            double pageTop = pi * (layout.PageH + PageGap);
            double px = ox + 0 * scale;
            double py = oy + pageTop * scale;
            double pw = layout.PageW * scale;
            double ph = layout.PageH * scale;

            ds.FillRectangle(new Rect(px + 3, py + 4, pw, ph), ColorHelper.FromArgb(40, 0, 0, 0));
            ds.FillRectangle(new Rect(px, py, pw, ph), Colors.White);
            ds.DrawRectangle(new Rect(px, py, pw, ph), border, 1f);

            // Celdas ocupadas (para dibujar punteado sólo en las vacías).
            var occupied = new bool[layout.Rows, layout.Cols];
            foreach (var pl in placements)
                for (int r = pl.Row; r < pl.Row + pl.RowSpan && r < layout.Rows; r++)
                    for (int c = pl.Col; c < pl.Col + pl.ColSpan && c < layout.Cols; c++)
                        occupied[r, c] = true;

            for (int r = 0; r < layout.Rows; r++)
                for (int c = 0; c < layout.Cols; c++)
                {
                    if (occupied[r, c]) continue;
                    double cx = layout.MarginLeft + c * (layout.CellW + layout.SpacingH);
                    double cy = pageTop + layout.MarginTop + r * (layout.CellH + layout.SpacingV);
                    var cell = new Rect(ox + cx * scale, oy + cy * scale, layout.CellW * scale, layout.CellH * scale);
                    ds.FillRectangle(cell, cellFill);
                    ds.DrawRectangle(cell, cellStroke, 1f, dashed);
                }

            // Imágenes.
            foreach (var pl in placements)
            {
                if (!byId.TryGetValue(pl.Image.Id, out var img)) continue;
                double cellX = layout.MarginLeft + pl.Col * (layout.CellW + layout.SpacingH);
                double cellY = pageTop + layout.MarginTop + pl.Row * (layout.CellH + layout.SpacingV);
                double spanW = pl.ColSpan * layout.CellW + (pl.ColSpan - 1) * layout.SpacingH;
                double spanH = pl.RowSpan * layout.CellH + (pl.RowSpan - 1) * layout.SpacingV;
                var dest = new Rect(ox + cellX * scale, oy + cellY * scale, spanW * scale, spanH * scale);

                DrawImageInCell(ds, img, dest);

                if (_selected.Contains(img))
                    ds.DrawRectangle(dest, selStroke, 2.5f);
            }
        }

        _lastScale = scale;
        _lastOx = ox;
        _lastOy = oy;
        _lastLayout = layout;
        _lastPages = pagePlacements;
    }

    /// <summary>Dibuja la imagen en su celda con fit (cover/contain/stretch), zoom interno y rotación 90°, recortada a la celda.</summary>
    private static void DrawImageInCell(CanvasDrawingSession ds, EditorImage img, Rect cell)
    {
        var bmp = img.Bitmap;
        float bw = (float)bmp.Size.Width, bh = (float)bmp.Size.Height;
        if (bw <= 0 || bh <= 0 || cell.Width <= 0 || cell.Height <= 0) return;

        bool quarter = ((int)Math.Round(img.RotationDeg / 90.0)) % 2 != 0;
        float ebw = quarter ? bh : bw;
        float ebh = quarter ? bw : bh;

        using var layer = ds.CreateLayer(1f, cell);
        var prev = ds.Transform;
        var center = new Vector2((float)(cell.X + cell.Width / 2), (float)(cell.Y + cell.Height / 2));
        var offset = new Vector2((float)(img.OffsetX * cell.Width), (float)(img.OffsetY * cell.Height));

        Matrix3x2 m;
        if (img.Fit == FitMode.Stretch)
        {
            float sx = (float)(cell.Width / ebw);
            float sy = (float)(cell.Height / ebh);
            m = Matrix3x2.CreateTranslation(-bw / 2f, -bh / 2f)
                * Matrix3x2.CreateScale(quarter ? sy : sx, quarter ? sx : sy)
                * Matrix3x2.CreateRotation((float)(img.RotationDeg * Math.PI / 180.0))
                * Matrix3x2.CreateTranslation(center + offset);
        }
        else
        {
            double sx = cell.Width / ebw, sy = cell.Height / ebh;
            double s = (img.Fit == FitMode.Contain ? Math.Min(sx, sy) : Math.Max(sx, sy)) * img.Zoom;
            m = Matrix3x2.CreateTranslation(-bw / 2f, -bh / 2f)
                * Matrix3x2.CreateScale((float)s)
                * Matrix3x2.CreateRotation((float)(img.RotationDeg * Math.PI / 180.0))
                * Matrix3x2.CreateTranslation(center + offset);
        }

        ds.Transform = m;
        ds.DrawImage(bmp, 0, 0);
        ds.Transform = prev;
    }

    // ---------- Chrome (label de zoom + hint) ----------

    private void UpdateChrome()
    {
        if (ZoomLabel is not null)
            ZoomLabel.Text = $"{Math.Round(_userZoom * 100)}%";
        if (EmptyHint is not null)
            EmptyHint.Visibility = _images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Inspector en vivo (panel izquierdo) ----------

    private static double ParseD(string? s, double fallback)
    {
        s = s?.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int ParseI(string? s, int fallback, int min, int max)
    {
        if (double.TryParse(s?.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return Math.Clamp((int)Math.Round(v), min, max);
        return fallback;
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _config.LayoutMode = ModeCombo.SelectedIndex switch
        {
            1 => LayoutModes.Count,
            2 => LayoutModes.Size,
            _ => LayoutModes.Grid,
        };
        GridPanel.Visibility = _config.LayoutMode == LayoutModes.Grid ? Visibility.Visible : Visibility.Collapsed;
        CountPanel.Visibility = _config.LayoutMode == LayoutModes.Count ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = _config.LayoutMode == LayoutModes.Size ? Visibility.Visible : Visibility.Collapsed;
        ApplyLayoutFields();
    }

    private void OnLayoutFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyLayoutFields();
    }

    private void OnMarginsToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        double m = MarginsToggle.IsOn ? 1.0 : 0.0; // cm (UI de 4 márgenes vendrá luego)
        _config.MarginTop = _config.MarginRight = _config.MarginBottom = _config.MarginLeft = m;
        PageCanvas.Invalidate();
    }

    private void ApplyLayoutFields()
    {
        _config.GridRows = ParseI(RowsBox?.Text, _config.GridRows, 1, 50);
        _config.GridCols = ParseI(ColsBox?.Text, _config.GridCols, 1, 50);
        _config.CountPerPage = ParseI(CountBox?.Text, _config.CountPerPage, 1, 200);
        _config.ImgWidth = Math.Max(0.1, ParseD(ImgWBox?.Text, _config.ImgWidth));
        _config.ImgHeight = Math.Max(0.1, ParseD(ImgHBox?.Text, _config.ImgHeight));
        _config.SpacingH = Math.Max(0, ParseD(SpacingHBox?.Text, _config.SpacingH));
        _config.SpacingV = Math.Max(0, ParseD(SpacingVBox?.Text, _config.SpacingV));
        PageCanvas.Invalidate();
    }
}
