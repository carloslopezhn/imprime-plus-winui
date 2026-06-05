using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Printing;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.Storage;
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

        // Ctrl+V para pegar imágenes del portapapeles.
        var paste = new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += async (_, args) => { args.Handled = true; await PasteAsync(); };
        KeyboardAccelerators.Add(paste);

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
                var files = Directory.GetFiles(dir)
                    .Where(f => ImageLoader.IsImage(f) || Archives.IsArchive(f))
                    .OrderBy(f => f).ToList();
                await AddPathsAsync(sender, files);
            }
        }
        else
        {
            // Device-lost: recargar los bitmaps desde su origen (ruta o bytes).
            foreach (var img in _images)
            {
                try
                {
                    if (img.SourcePath is not null)
                        img.Bitmap = (await ImageLoader.LoadFromFileAsync(sender, img.SourcePath)) ?? img.Bitmap;
                    else if (img.SourceBytes is not null)
                        img.Bitmap = (await ImageLoader.LoadFromBytesAsync(sender, img.SourceBytes)) ?? img.Bitmap;
                }
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

        // Hook de verificación de estilos/filtros (Fase 6).
        if (Environment.GetEnvironmentVariable("IMPRIME_DEV_STYLE") == "1" && _images.Count >= 5)
        {
            _images[0].Shape = ImageShape.Circle;
            _images[1].Shape = ImageShape.Rounded;
            _images[1].BorderWidth = 6; _images[1].BorderColor = ColorHelper.FromArgb(255, 0x3B, 0x82, 0xF6);
            _images[2].Shape = ImageShape.Hexagon; _images[2].Sepia = 1.0;
            _images[3].Shape = ImageShape.Star; _images[3].Grayscale = 1.0;
            _images[4].Saturation = 1.8; _images[4].Shadow = ShadowStrength.Strong;
            _images[4].CaptionText = "Foto 5"; _images[4].CaptionPos = CaptionPosition.Overlay;
            _selected.Clear(); _selected.Add(_images[0]);
            UpdateInspector();
            sender.Invalidate();
        }

        // Test de impresión headless: render de la página 1 a 300 DPI (lo que recibe
        // la impresora) y guardado a PNG, para verificar nitidez vectorial sin diálogo.
        if (Environment.GetEnvironmentVariable("IMPRIME_DEV_PRINTTEST") == "1" && _images.Count > 0)
        {
            const float dpi = 300f;
            const double wIn = 8.5, hIn = 11.0;            // Carta
            double wDip = wIn * 96, hDip = hIn * 96;
            double m = 0.25 * 96;                          // margen imprimible 0.25"
            using var rt = new CanvasRenderTarget(sender.Device, (float)wDip, (float)hDip, dpi);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(Colors.White);
                DrawPrintPage(ds, 1, new Rect(m, m, wDip - 2 * m, hDip - 2 * m));
            }
            var outPath = Path.Combine(AppContext.BaseDirectory, "_shot_print_render.png");
            await rt.SaveAsync(outPath, CanvasBitmapFileFormat.Png);
        }
    }

    // ---------- Carga de imágenes (archivo / comprimido / bytes) ----------

    private async void OnAddImages(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        foreach (var ext in ImageLoader.ImageExts) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            await AddPathsAsync(PageCanvas, files.Select(f => f.Path));
    }

    private async void OnAddArchive(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (var ext in new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            await AddPathsAsync(PageCanvas, files.Select(f => f.Path));
    }

    /// <summary>Agrega imágenes desde rutas (imágenes sueltas y/o comprimidos).</summary>
    private async Task AddPathsAsync(ICanvasResourceCreator rc, IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (Archives.IsArchive(path))
            {
                foreach (var (name, data) in Archives.ExtractImages(path))
                {
                    var bmp = await ImageLoader.LoadFromBytesAsync(rc, data);
                    if (bmp is not null)
                    {
                        _images.Add(new EditorImage($"img{++_idSeq}", bmp) { SourcePath = null, SourceBytes = data });
                        added++;
                    }
                }
            }
            else if (ImageLoader.IsImage(path))
            {
                var bmp = await ImageLoader.LoadFromFileAsync(rc, path);
                if (bmp is not null)
                {
                    _images.Add(new EditorImage($"img{++_idSeq}", bmp) { SourcePath = path });
                    added++;
                }
            }
        }
        if (added > 0) { UpdateChrome(); PageCanvas.Invalidate(); }
    }

    private async Task AddBytesAsync(ICanvasResourceCreator rc, byte[] data)
    {
        var bmp = await ImageLoader.LoadFromBytesAsync(rc, data);
        if (bmp is not null)
        {
            _images.Add(new EditorImage($"img{++_idSeq}", bmp) { SourcePath = null, SourceBytes = data });
            UpdateChrome();
            PageCanvas.Invalidate();
        }
    }

    // ---------- Pegar (Ctrl+V / botón) ----------

    private async void OnPaste(object sender, RoutedEventArgs e) => await PasteAsync();

    private async Task PasteAsync()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                await AddPathsAsync(PageCanvas, items.OfType<StorageFile>().Select(f => f.Path));
            }
            else if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                var streamRef = await view.GetBitmapAsync();
                using var stream = await streamRef.OpenReadAsync();
                using var net = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await net.CopyToAsync(ms);
                await AddBytesAsync(PageCanvas, ms.ToArray());
            }
        }
        catch { /* portapapeles sin imagen */ }
    }

    // ---------- Drag & drop ----------

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Agregar a Imprime+";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var def = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await AddPathsAsync(PageCanvas, items.OfType<StorageFile>().Select(f => f.Path));
        }
        finally { def.Complete(); }
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
        UpdateInspector();
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
                UpdateInspector();
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
                UpdateInspector();
                PageCanvas.Invalidate();
            }
            else
            {
                if (!ctrl) _selected.Clear();
                BeginPan(e);
                UpdateInspector();
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
        UpdateInspector();
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

                ImageRenderer.Draw(ds, img, dest, scale);

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

    // ---------- Inspector per-imagen (panel derecho, en vivo) ----------

    private bool _syncingInspector;

    private EditorImage? Primary => _images.FirstOrDefault(i => _selected.Contains(i));

    private void UpdateInspector()
    {
        var img = Primary;
        bool has = img is not null;
        if (InspectorEmpty is not null) InspectorEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (InspectorContent is not null) InspectorContent.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (img is null) return;

        _syncingInspector = true;
        InsFit.SelectedIndex = (int)img.Fit;
        InsZoom.Value = img.Zoom * 100;
        InsShape.SelectedIndex = (int)img.Shape;
        InsRadius.Value = img.CornerRadius;
        InsBorder.Value = img.BorderWidth;
        SetSwatch(img.BorderColor);
        InsBorderPicker.Color = img.BorderColor;
        InsBrightness.Value = img.Brightness * 100;
        InsContrast.Value = img.Contrast * 100;
        InsSaturation.Value = img.Saturation * 100;
        InsGrayscale.Value = img.Grayscale * 100;
        InsSepia.Value = img.Sepia * 100;
        InsCaptionText.Text = img.CaptionText;
        InsCaptionPos.SelectedIndex = (int)img.CaptionPos;
        _syncingInspector = false;
    }

    private void SetSwatch(Color c)
    {
        if (InsBorderSwatch is not null) InsBorderSwatch.Background = new SolidColorBrush(c);
    }

    private void ApplyToSelected(Action<EditorImage> apply)
    {
        if (_selected.Count == 0) return;
        foreach (var img in _selected) apply(img);
        PageCanvas.Invalidate();
    }

    private void OnInsFitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInspector || InsFit.SelectedIndex < 0) return;
        ApplyToSelected(i => i.Fit = (FitMode)InsFit.SelectedIndex);
    }

    private void OnInsZoomChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => i.Zoom = InsZoom.Value / 100.0);
    }

    private void OnInsRotate(object sender, RoutedEventArgs e) => RotateSelected();

    private void OnInsShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInspector || InsShape.SelectedIndex < 0) return;
        ApplyToSelected(i => i.Shape = (ImageShape)InsShape.SelectedIndex);
    }

    private void OnInsRadiusChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => i.CornerRadius = InsRadius.Value);
    }

    private void OnInsBorderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => i.BorderWidth = InsBorder.Value);
    }

    private void OnInsBorderColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingInspector) return;
        SetSwatch(args.NewColor);
        ApplyToSelected(i => i.BorderColor = args.NewColor);
    }

    private void OnInsFilterChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i =>
        {
            i.Brightness = InsBrightness.Value / 100.0;
            i.Contrast = InsContrast.Value / 100.0;
            i.Saturation = InsSaturation.Value / 100.0;
            i.Grayscale = InsGrayscale.Value / 100.0;
            i.Sepia = InsSepia.Value / 100.0;
        });
    }

    private void OnInsResetFilters(object sender, RoutedEventArgs e)
    {
        ApplyToSelected(i => { i.Brightness = 1; i.Contrast = 1; i.Saturation = 1; i.Grayscale = 0; i.Sepia = 0; });
        UpdateInspector();
    }

    private void OnInsCaptionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => i.CaptionText = InsCaptionText.Text);
    }

    private void OnInsCaptionPosChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInspector || InsCaptionPos.SelectedIndex < 0) return;
        ApplyToSelected(i => i.CaptionPos = (CaptionPosition)InsCaptionPos.SelectedIndex);
    }

    // ---------- Impresión vectorial (CanvasPrintDocument) ----------

    private CanvasPrintDocument? _printDoc;

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_images.Count == 0) return;
        var hwnd = App.WindowHandle;
        var manager = Windows.Graphics.Printing.PrintManagerInterop.GetForWindow(hwnd);
        manager.PrintTaskRequested += OnPrintTaskRequested;
        try
        {
            await Windows.Graphics.Printing.PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        catch { /* el usuario canceló o la impresora falló */ }
        finally
        {
            manager.PrintTaskRequested -= OnPrintTaskRequested;
        }
    }

    private void OnPrintTaskRequested(Windows.Graphics.Printing.PrintManager sender,
        Windows.Graphics.Printing.PrintTaskRequestedEventArgs args)
    {
        // El print doc comparte el device del lienzo para reusar los mismos bitmaps GPU.
        _printDoc = new CanvasPrintDocument(PageCanvas.Device);
        _printDoc.PrintTaskOptionsChanged += (doc, a) => doc.SetPageCount((uint)ComputePrintPageCount());
        _printDoc.Preview += (doc, a) =>
        {
            var desc = a.PrintTaskOptions.GetPageDescription(a.PageNumber);
            DrawPrintPage(a.DrawingSession, (int)a.PageNumber, desc.ImageableRect);
        };
        _printDoc.Print += (doc, a) =>
        {
            int count = ComputePrintPageCount();
            for (uint p = 1; p <= count; p++)
            {
                var desc = a.PrintTaskOptions.GetPageDescription(p);
                using var ds = a.CreateDrawingSession();
                DrawPrintPage(ds, (int)p, desc.ImageableRect);
            }
        };

        args.Request.CreatePrintTask("Imprime+", req => req.SetSource(_printDoc));
    }

    private int ComputePrintPageCount()
    {
        var layout = LayoutEngine.ComputeLayout(_config);
        var items = _images.Select(i => (ImageItem?)i.ToItem()).ToList();
        return LayoutEngine.Paginate(items, layout).Count;
    }

    /// <summary>
    /// Dibuja la página <paramref name="pageNumber"/> dentro del área imprimible
    /// <paramref name="imageable"/> (DIPs). Misma escena que el editor → WYSIWYG y
    /// salida VECTORIAL (texto/formas) + fotos a la resolución real de la impresora.
    /// </summary>
    private void DrawPrintPage(CanvasDrawingSession ds, int pageNumber, Rect imageable)
    {
        var layout = LayoutEngine.ComputeLayout(_config);
        if (layout.PageW <= 0 || layout.PageH <= 0) return;

        var items = _images.Select(i => (ImageItem?)i.ToItem()).ToList();
        var pages = LayoutEngine.Paginate(items, layout);
        if (pageNumber < 1 || pageNumber > pages.Count) return;

        var placements = LayoutEngine.PlacePage(pages[pageNumber - 1].Images, layout.Cols, layout.Rows);

        double scale = Math.Min(imageable.Width / layout.PageW, imageable.Height / layout.PageH);
        if (scale <= 0 || double.IsNaN(scale)) return;
        double ox = imageable.X + (imageable.Width - layout.PageW * scale) / 2.0;
        double oy = imageable.Y + (imageable.Height - layout.PageH * scale) / 2.0;

        var byId = _images.ToDictionary(i => i.Id);
        foreach (var pl in placements)
        {
            if (!byId.TryGetValue(pl.Image.Id, out var img)) continue;
            double cellX = layout.MarginLeft + pl.Col * (layout.CellW + layout.SpacingH);
            double cellY = layout.MarginTop + pl.Row * (layout.CellH + layout.SpacingV);
            double spanW = pl.ColSpan * layout.CellW + (pl.ColSpan - 1) * layout.SpacingH;
            double spanH = pl.RowSpan * layout.CellH + (pl.RowSpan - 1) * layout.SpacingV;
            var dest = new Rect(ox + cellX * scale, oy + cellY * scale, spanW * scale, spanH * scale);
            ImageRenderer.Draw(ds, img, dest, scale);
        }
    }
}
