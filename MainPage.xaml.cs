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
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
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

    // Configuración por defecto (Carta 3x3). Se carga de AppData al iniciar.
    private LayoutConfig _config = new()
    {
        Unit = Units.Cm,
        PageWidth = 21.59,
        PageHeight = 27.94,
        SpacingH = 0.3,
        SpacingV = 0.3,
        LayoutMode = LayoutModes.Grid,
        GridRows = 3,
        GridCols = 3,
        CountPerPage = 9,
        ImgWidth = 5,
        ImgHeight = 5,
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

    // Arrastrar imagen para reordenar entre celdas.
    private bool _maybeDrag, _dragging;
    private EditorImage? _dragImage, _dragTarget;
    private Point _dragStart;

    private int _idSeq;

    // Modo póster: 1 imagen dividida en _posterCols x _posterRows páginas.
    private bool _posterEnabled;
    private int _posterCols = 2, _posterRows = 2;
    private bool IsPoster => _posterEnabled && _images.Count > 0;
    private int PosterPageCount => Math.Max(1, _posterCols) * Math.Max(1, _posterRows);

    // Página visible (vista de una página + barra de navegación inferior).
    private int _currentPage;

    // Defaults globales de imagen (sección "Imágenes (global)" del panel izquierdo).
    private readonly GlobalDefaults _global = new();

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

        // Cargar config persistida y reflejarla en el panel izquierdo (con _ready=false
        // para que setear los controles no dispare guardados redundantes).
        var saved = SettingsStore.Load();
        if (saved is not null) _config = saved;
        SyncLeftPanelFromConfig();
        UpdatePageSummary();
        PopulatePrinters();

        _ready = true; // a partir de aquí los eventos del inspector ya son del usuario
    }

    private void SyncLeftPanelFromConfig()
    {
        var inv = CultureInfo.InvariantCulture;
        ModeCombo.SelectedIndex = _config.LayoutMode switch
        {
            LayoutModes.Count => 1,
            LayoutModes.Size => 2,
            _ => 0,
        };
        GridPanel.Visibility = _config.LayoutMode == LayoutModes.Grid ? Visibility.Visible : Visibility.Collapsed;
        CountPanel.Visibility = _config.LayoutMode == LayoutModes.Count ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = _config.LayoutMode == LayoutModes.Size ? Visibility.Visible : Visibility.Collapsed;
        RowsBox.Text = _config.GridRows.ToString(inv);
        ColsBox.Text = _config.GridCols.ToString(inv);
        CountBox.Text = _config.CountPerPage.ToString(inv);
        ImgWBox.Text = _config.ImgWidth.ToString(inv);
        ImgHBox.Text = _config.ImgHeight.ToString(inv);
        SpacingHBox.Text = _config.SpacingH.ToString(inv);
        SpacingVBox.Text = _config.SpacingV.ToString(inv);
        bool hasMargins = _config.MarginTop > 0 || _config.MarginBottom > 0 || _config.MarginLeft > 0 || _config.MarginRight > 0;
        MarginsToggle.IsOn = hasMargins;
        MarginsPanel.Visibility = hasMargins ? Visibility.Visible : Visibility.Collapsed;
        if (hasMargins)
        {
            MarginTopBox.Text = _config.MarginTop.ToString(inv);
            MarginBottomBox.Text = _config.MarginBottom.ToString(inv);
            MarginLeftBox.Text = _config.MarginLeft.ToString(inv);
            MarginRightBox.Text = _config.MarginRight.ToString(inv);
        }
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

            // Archivos pasados por línea de comandos (asociación de archivos / "abrir con").
            var cliArgs = Environment.GetCommandLineArgs().Skip(1)
                .Where(a => File.Exists(a) && (ImageLoader.IsImage(a) || Archives.IsArchive(a)))
                .ToList();
            if (cliArgs.Count > 0) await AddPathsAsync(sender, cliArgs);
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

        // Test de export PDF headless.
        if (Environment.GetEnvironmentVariable("IMPRIME_DEV_PDFTEST") == "1" && _images.Count > 0)
        {
            await ExportPdfAsync(Path.Combine(AppContext.BaseDirectory, "_test_export.pdf"), 300);
        }

        // Test de modo póster headless.
        if (Environment.GetEnvironmentVariable("IMPRIME_DEV_POSTER") == "1" && _images.Count > 0)
        {
            _selected.Clear(); _selected.Add(_images[0]);
            PosterToggle.IsOn = true; // dispara OnPosterToggled
            sender.Invalidate();
        }

        // Test de quita-fondo headless: recorta la imagen 0 y la compone sobre magenta.
        if (Environment.GetEnvironmentVariable("IMPRIME_DEV_BGTEST") == "1" && _images.Count > 0)
        {
            var cut = await RemoveBgCoreAsync(_images[0]);
            int w = (int)cut.SizeInPixels.Width, h = (int)cut.SizeInPixels.Height;
            using var rt = new CanvasRenderTarget(sender, w, h, 96);
            using (var ds = rt.CreateDrawingSession()) { ds.Clear(Colors.Magenta); ds.DrawImage(cut); }
            await rt.SaveAsync(Path.Combine(AppContext.BaseDirectory, "_shot_bg.png"), CanvasBitmapFileFormat.Png);
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
        double factor = delta > 0 ? 1.12 : 1 / 1.12;
        if (_selected.Count > 0)
        {
            // Hay imagen(es) seleccionada(s): la rueda hace zoom INTERNO de la imagen.
            foreach (var img in _selected) img.Zoom = Math.Clamp(img.Zoom * factor, 0.2, 6.0);
            UpdateInspector();
            PageCanvas.Invalidate();
        }
        else
        {
            // Sin selección: la rueda hace zoom a la PÁGINA.
            ZoomAround(p.Position, factor);
        }
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
        // Tomar foco de teclado para que las flechas muevan la imagen seleccionada.
        PageCanvas.Focus(FocusState.Pointer);

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
                // Preparar arrastre-para-reordenar.
                _dragImage = hit; _maybeDrag = true; _dragging = false; _dragTarget = null; _dragStart = p.Position;
                PageCanvas.CapturePointer(e.Pointer);
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
        if (_panning)
        {
            var pp = e.GetCurrentPoint(PageCanvas).Position;
            _panX += pp.X - _lastPointer.X;
            _panY += pp.Y - _lastPointer.Y;
            _lastPointer = pp;
            PageCanvas.Invalidate();
            return;
        }
        if (_maybeDrag)
        {
            var pos = e.GetCurrentPoint(PageCanvas).Position;
            if (!_dragging && Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y) > 8)
            {
                _dragging = true;
                _lastPointer = pos; // arrancar el delta interno desde aquí
            }
            if (_dragging)
            {
                var t = HitTest(pos);
                if (t == _dragImage && _dragImage is not null)
                {
                    // Arrastre DENTRO de la propia celda → mover la imagen internamente.
                    var span = CurrentSpanScreen(_dragImage);
                    if (span is { } sp && sp.w > 0 && sp.h > 0)
                    {
                        double dx = pos.X - _lastPointer.X;
                        double dy = pos.Y - _lastPointer.Y;
                        _dragImage.OffsetX = Math.Clamp(_dragImage.OffsetX + dx / sp.w, -2.0, 2.0);
                        _dragImage.OffsetY = Math.Clamp(_dragImage.OffsetY + dy / sp.h, -2.0, 2.0);
                        UpdateInspector();
                    }
                    _dragTarget = null;
                }
                else
                {
                    // Arrastre a OTRA celda → reordenar (intercambiar posición).
                    _dragTarget = (t != _dragImage) ? t : null;
                }
                _lastPointer = pos;
                PageCanvas.Invalidate();
            }
        }
    }

    // Tamaño en pantalla (px) de la celda/span donde se dibuja una imagen.
    private (double w, double h)? CurrentSpanScreen(EditorImage img)
    {
        if (_lastLayout is null) return null;
        var L = _lastLayout;
        foreach (var page in _lastPages)
            foreach (var pl in page)
                if (pl.Image.Id == img.Id)
                {
                    double spanW = pl.ColSpan * L.CellW + (pl.ColSpan - 1) * L.SpacingH;
                    double spanH = pl.RowSpan * L.CellH + (pl.RowSpan - 1) * L.SpacingV;
                    return (spanW * _lastScale, spanH * _lastScale);
                }
        return null;
    }

    private void OnCanvasReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            PageCanvas.ReleasePointerCapture(e.Pointer);
            return;
        }
        if (_maybeDrag)
        {
            _maybeDrag = false;
            PageCanvas.ReleasePointerCapture(e.Pointer);
            if (_dragging && _dragImage is not null && _dragTarget is not null && _dragTarget != _dragImage)
                ReorderImage(_dragImage, _dragTarget);
            _dragging = false; _dragImage = null; _dragTarget = null;
            PageCanvas.Invalidate();
        }
    }

    private void ReorderImage(EditorImage src, EditorImage target)
    {
        if (!_images.Remove(src)) return;
        int ti = _images.IndexOf(target);
        if (ti < 0) ti = _images.Count;
        _images.Insert(ti, src);
        UpdateChrome();
    }

    // Flechas del teclado: mover internamente la imagen seleccionada (paso fino,
    // Shift = paso grande). Como la rueda hace zoom interno, esto cierra el control
    // completo de posición de la foto dentro de su celda.
    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_selected.Count == 0) return;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        double step = shift ? 0.05 : 0.01;
        double dx = 0, dy = 0;
        switch (e.Key)
        {
            case VirtualKey.Left:  dx = -step; break;
            case VirtualKey.Right: dx =  step; break;
            case VirtualKey.Up:    dy = -step; break;
            case VirtualKey.Down:  dy =  step; break;
            default: return;
        }
        foreach (var img in _selected)
        {
            img.OffsetX = Math.Clamp(img.OffsetX + dx, -2.0, 2.0);
            img.OffsetY = Math.Clamp(img.OffsetY + dy, -2.0, 2.0);
        }
        UpdateInspector();
        PageCanvas.Invalidate();
        e.Handled = true;
    }

    // ---------- Menú contextual ----------

    private void ShowContextMenu(Point at)
    {
        var menu = new MenuFlyout();

        var bg = new MenuFlyoutItem { Text = "Borrar fondo (IA)" };
        bg.Click += (_, _) => OnRemoveBg(this, new RoutedEventArgs());
        menu.Items.Add(bg);

        menu.Items.Add(new MenuFlyoutSeparator());

        var rot = new MenuFlyoutItem { Text = "Rotar 90°" };
        rot.Click += (_, _) => RotateSelected();
        menu.Items.Add(rot);

        var dup = new MenuFlyoutItem { Text = "Duplicar" };
        dup.Click += (_, _) => DuplicateSelected();
        menu.Items.Add(dup);

        var mul = new MenuFlyoutItem { Text = "Multiplicar…" };
        mul.Click += (_, _) => MultiplySelectedAsync();
        menu.Items.Add(mul);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exH = new MenuFlyoutItem { Text = "Ampliar horizontalmente" };
        exH.Click += (_, _) => ExpandSelected(horizontal: true);
        menu.Items.Add(exH);

        var exV = new MenuFlyoutItem { Text = "Ampliar verticalmente" };
        exV.Click += (_, _) => ExpandSelected(horizontal: false);
        menu.Items.Add(exV);

        var reset = new MenuFlyoutItem { Text = "Restablecer tamaño (1x1)" };
        reset.Click += (_, _) => ResetSpanSelected();
        menu.Items.Add(reset);

        menu.Items.Add(new MenuFlyoutSeparator());

        var del = new MenuFlyoutItem { Text = "Limpiar espacio (eliminar)" };
        del.Click += (_, _) => DeleteSelected();
        menu.Items.Add(del);

        menu.ShowAt(PageCanvas, new FlyoutShowOptions { Position = at });
    }

    private EditorImage CloneImage(EditorImage img)
    {
        var c = new EditorImage($"img{++_idSeq}", img.Bitmap)
        {
            SourcePath = img.SourcePath, SourceBytes = img.SourceBytes,
            Fit = img.Fit, Zoom = img.Zoom, OffsetX = img.OffsetX, OffsetY = img.OffsetY, RotationDeg = img.RotationDeg,
            Shape = img.Shape, CornerRadius = img.CornerRadius, BorderWidth = img.BorderWidth,
            BorderColor = img.BorderColor, BackgroundColor = img.BackgroundColor, Shadow = img.Shadow,
            Brightness = img.Brightness, Contrast = img.Contrast, Saturation = img.Saturation,
            Grayscale = img.Grayscale, Sepia = img.Sepia, Hue = img.Hue, Blur = img.Blur, Invert = img.Invert, Opacity = img.Opacity,
            CaptionText = img.CaptionText, CaptionPos = img.CaptionPos, CaptionFont = img.CaptionFont,
            CaptionSize = img.CaptionSize, CaptionColor = img.CaptionColor, CaptionBg = img.CaptionBg,
        };
        c.Overrides.ColSpan = img.Overrides.ColSpan;
        c.Overrides.RowSpan = img.Overrides.RowSpan;
        return c;
    }

    private void DuplicateSelected()
    {
        int added = 0;
        foreach (var img in _images.Where(_selected.Contains).ToList())
        {
            int idx = _images.IndexOf(img);
            _images.Insert(idx + 1, CloneImage(img));
            added++;
        }
        if (added > 0) { UpdateChrome(); PageCanvas.Invalidate(); }
    }

    private void ResetSpanSelected()
    {
        foreach (var img in _selected) { img.Overrides.ColSpan = 1; img.Overrides.RowSpan = 1; }
        if (_selected.Count > 0) PageCanvas.Invalidate();
    }

    private async void MultiplySelectedAsync()
    {
        if (_selected.Count == 0) return;
        var box = new TextBox { Text = "2", Width = 120 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "¿Cuántas copias de cada imagen seleccionada?", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dlg = new ContentDialog
        {
            Title = "Multiplicar",
            Content = panel,
            PrimaryButtonText = "Multiplicar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        int n = ParseI(box.Text, 1, 1, 500);

        int added = 0;
        foreach (var img in _images.Where(_selected.Contains).ToList())
        {
            int idx = _images.IndexOf(img);
            for (int k = 0; k < n; k++) { _images.Insert(idx + 1 + k, CloneImage(img)); added++; }
        }
        if (added > 0) { UpdateChrome(); PageCanvas.Invalidate(); }
    }

    private void RotateSelected()
    {
        foreach (var img in _selected)
            img.RotationDeg = (img.RotationDeg + 90) % 360;
        if (_selected.Count > 0) PageCanvas.Invalidate();
    }

    private void ExpandSelected(bool horizontal)
    {
        var layout = LayoutEngine.ComputeLayout(_config);
        foreach (var img in _selected)
        {
            if (horizontal)
                img.Overrides.ColSpan = img.Overrides.ColSpan < layout.Cols ? img.Overrides.ColSpan + 1 : 1;
            else
                img.Overrides.RowSpan = img.Overrides.RowSpan < layout.Rows ? img.Overrides.RowSpan + 1 : 1;
        }
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

        if (IsPoster) { DrawPosterScene(ds, size, layout); return; }

        var items = _images.Select(i => (ImageItem?)i.ToItem()).ToList();
        var pages = LayoutEngine.Paginate(items, layout);
        int nPages = pages.Count;
        _currentPage = Math.Clamp(_currentPage, 0, nPages - 1);
        UpdatePageNav(nPages);

        // Vista de UNA página (como la versión vieja: navegación con la barra inferior).
        double worldW = layout.PageW, worldH = layout.PageH;
        double baseScale = Math.Min((size.Width - 2 * Pad) / worldW, (size.Height - 2 * Pad) / worldH);
        if (baseScale <= 0 || double.IsNaN(baseScale) || double.IsInfinity(baseScale)) baseScale = 0.05;
        double scale = baseScale * _userZoom;
        double ox = (size.Width - worldW * scale) / 2.0 + _panX;
        double oy = (size.Height - worldH * scale) / 2.0 + _panY;

        var byId = _images.ToDictionary(i => i.Id);
        var placements = LayoutEngine.PlacePage(pages[_currentPage].Images, layout.Cols, layout.Rows);

        Color border = ColorHelper.FromArgb(255, 0xC7, 0xD2, 0xE0);
        Color guide = ColorHelper.FromArgb(200, 0x9C, 0xA8, 0xB8);
        Color selStroke = ColorHelper.FromArgb(255, 0x25, 0x63, 0xEB);

        double px = ox, py = oy, pw = layout.PageW * scale, ph = layout.PageH * scale;
        ds.FillRectangle(new Rect(px + 3, py + 4, pw, ph), ColorHelper.FromArgb(40, 0, 0, 0));
        ds.FillRectangle(new Rect(px, py, pw, ph), Colors.White);
        ds.DrawRectangle(new Rect(px, py, pw, ph), border, 1f);

        var occupied = new bool[layout.Rows, layout.Cols];
        foreach (var pl in placements)
            for (int r = pl.Row; r < pl.Row + pl.RowSpan && r < layout.Rows; r++)
                for (int c = pl.Col; c < pl.Col + pl.ColSpan && c < layout.Cols; c++)
                    occupied[r, c] = true;

        // Guías de corte: cruz "+" en el centro de cada celda vacía (como la vieja).
        for (int r = 0; r < layout.Rows; r++)
            for (int c = 0; c < layout.Cols; c++)
            {
                if (occupied[r, c]) continue;
                double ccx = layout.MarginLeft + c * (layout.CellW + layout.SpacingH) + layout.CellW / 2;
                double ccy = layout.MarginTop + r * (layout.CellH + layout.SpacingV) + layout.CellH / 2;
                double len = Math.Clamp(Math.Min(layout.CellW, layout.CellH) * scale * 0.08, 4, 14);
                float sx = (float)(ox + ccx * scale), sy = (float)(oy + ccy * scale);
                ds.DrawLine(sx - (float)len, sy, sx + (float)len, sy, guide, 1f);
                ds.DrawLine(sx, sy - (float)len, sx, sy + (float)len, guide, 1f);
            }

        foreach (var pl in placements)
        {
            if (!byId.TryGetValue(pl.Image.Id, out var img)) continue;
            double cellX = layout.MarginLeft + pl.Col * (layout.CellW + layout.SpacingH);
            double cellY = layout.MarginTop + pl.Row * (layout.CellH + layout.SpacingV);
            double spanW = pl.ColSpan * layout.CellW + (pl.ColSpan - 1) * layout.SpacingH;
            double spanH = pl.RowSpan * layout.CellH + (pl.RowSpan - 1) * layout.SpacingV;
            var dest = new Rect(ox + cellX * scale, oy + cellY * scale, spanW * scale, spanH * scale);
            ImageRenderer.Draw(ds, img, dest, scale, _global);
            if (_global.CutGuides) DrawCutMarks(ds, dest);
            if (_dragging && img == _dragTarget)
                ds.DrawRectangle(dest, ColorHelper.FromArgb(255, 0x16, 0xA3, 0x4A), 4f); // destino de reordenamiento
            else if (_selected.Contains(img))
                ds.DrawRectangle(dest, selStroke, 2.5f);
        }

        _lastScale = scale;
        _lastOx = ox;
        _lastOy = oy;
        _lastLayout = layout;
        _lastPages = new List<List<Placement>> { placements };
    }

    private static void DrawCutMarks(CanvasDrawingSession ds, Rect r)
    {
        Color c = ColorHelper.FromArgb(255, 0x33, 0x33, 0x33);
        const float len = 9f, w = 0.8f;
        float x0 = (float)r.X, y0 = (float)r.Y, x1 = (float)(r.X + r.Width), y1 = (float)(r.Y + r.Height);
        // Marcas en L en las 4 esquinas.
        ds.DrawLine(x0, y0, x0 + len, y0, c, w); ds.DrawLine(x0, y0, x0, y0 + len, c, w);
        ds.DrawLine(x1, y0, x1 - len, y0, c, w); ds.DrawLine(x1, y0, x1, y0 + len, c, w);
        ds.DrawLine(x0, y1, x0 + len, y1, c, w); ds.DrawLine(x0, y1, x0, y1 - len, c, w);
        ds.DrawLine(x1, y1, x1 - len, y1, c, w); ds.DrawLine(x1, y1, x1, y1 - len, c, w);
    }

    // ---------- Modo póster ----------

    private void DrawPosterScene(CanvasDrawingSession ds, Size size, LayoutResult layout)
    {
        var src = Primary ?? _images[0];
        int n = PosterPageCount;
        _currentPage = Math.Clamp(_currentPage, 0, n - 1);
        UpdatePageNav(n);

        double worldW = layout.PageW, worldH = layout.PageH; // una página a la vez
        double baseScale = Math.Min((size.Width - 2 * Pad) / worldW, (size.Height - 2 * Pad) / worldH);
        if (baseScale <= 0 || double.IsNaN(baseScale) || double.IsInfinity(baseScale)) baseScale = 0.05;
        double scale = baseScale * _userZoom;
        double ox = (size.Width - worldW * scale) / 2.0 + _panX;
        double oy = (size.Height - worldH * scale) / 2.0 + _panY;

        Color border = ColorHelper.FromArgb(255, 0xC7, 0xD2, 0xE0);
        Color tag = ColorHelper.FromArgb(255, 0x64, 0x74, 0x8B);

        double px = ox, py = oy, pw = layout.PageW * scale, ph = layout.PageH * scale;
        ds.FillRectangle(new Rect(px + 3, py + 4, pw, ph), ColorHelper.FromArgb(40, 0, 0, 0));
        ds.FillRectangle(new Rect(px, py, pw, ph), Colors.White);
        ds.DrawRectangle(new Rect(px, py, pw, ph), border, 1f);
        DrawPosterTile(ds, src, layout, _currentPage, ox, oy, scale, 0);
        int pcol = _currentPage % _posterCols, prow = _currentPage / _posterCols;
        using (var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = (float)Math.Max(9, 11 * scale) })
            ds.DrawText($"{pcol + 1},{prow + 1}", (float)(px + 6), (float)(py + 4), tag, fmt);

        _lastScale = scale; _lastOx = ox; _lastOy = oy; _lastLayout = layout; _lastPages = new List<List<Placement>>();
    }

    /// <summary>Dibuja el recorte de póster que corresponde a la página <paramref name="pageIndex"/>.</summary>
    private void DrawPosterTile(CanvasDrawingSession ds, EditorImage src, LayoutResult L, int pageIndex,
        double ox, double oy, double scale, double pageTopWorld)
    {
        int col = pageIndex % _posterCols, row = pageIndex / _posterCols;
        double posterW = _posterCols * L.ContentW, posterH = _posterRows * L.ContentH;
        double bw = src.Bitmap.Size.Width, bh = src.Bitmap.Size.Height;
        if (bw <= 0 || bh <= 0 || posterW <= 0 || posterH <= 0) return;

        double s = Math.Max(posterW / bw, posterH / bh); // cover de la imagen sobre todo el póster
        double iw = bw * s, ih = bh * s;
        double posterX0 = (posterW - iw) / 2, posterY0 = (posterH - ih) / 2;
        double cx = L.MarginLeft, cy = pageTopWorld + L.MarginTop;
        double imgX = cx - col * L.ContentW + posterX0;
        double imgY = cy - row * L.ContentH + posterY0;

        var contentScreen = new Rect(ox + cx * scale, oy + cy * scale, L.ContentW * scale, L.ContentH * scale);
        var imgScreen = new Rect(ox + imgX * scale, oy + imgY * scale, iw * scale, ih * scale);
        using (ds.CreateLayer(1f, contentScreen))
            ds.DrawImage(src.Bitmap, imgScreen);
    }

    // ---------- Chrome (label de zoom + hint) ----------

    private void UpdateChrome()
    {
        if (ZoomLabel is not null)
            ZoomLabel.Text = $"{Math.Round(_userZoom * 100)}%";
        if (EmptyHint is not null)
            EmptyHint.Visibility = _images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Navegación de páginas (barra inferior) ----------

    private void UpdatePageNav(int total)
    {
        if (PageNavBar is null) return;
        bool show = _images.Count > 0;
        PageNavBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        total = Math.Max(1, total);
        PageNavLabel.Text = $"Página {_currentPage + 1} de {total}";
        NavFirst.IsEnabled = NavPrev.IsEnabled = _currentPage > 0;
        NavNext.IsEnabled = NavLast.IsEnabled = _currentPage < total - 1;
    }

    private void OnNavFirst(object sender, RoutedEventArgs e) { _currentPage = 0; PageCanvas.Invalidate(); }
    private void OnNavPrev(object sender, RoutedEventArgs e) { if (_currentPage > 0) _currentPage--; PageCanvas.Invalidate(); }
    private void OnNavNext(object sender, RoutedEventArgs e) { _currentPage++; PageCanvas.Invalidate(); }
    private void OnNavLast(object sender, RoutedEventArgs e) { _currentPage = int.MaxValue; PageCanvas.Invalidate(); }

    // ---------- Configuración de página (modal + presets) ----------

    private string _pageName = "Carta";

    private void UpdatePageSummary()
    {
        if (PageSummary is null) return;
        var inv = CultureInfo.InvariantCulture;
        PageSummary.Text = $"{_pageName} — {_config.PageWidth.ToString("0.##", inv)} x {_config.PageHeight.ToString("0.##", inv)} {_config.Unit}";
    }

    private async void OnConfigPage(object sender, RoutedEventArgs e)
    {
        var inv = CultureInfo.InvariantCulture;
        var presets = await PresetService.GetAsync();

        var presetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = presets, PlaceholderText = "Personalizado" };
        var unitCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        unitCombo.Items.Add("Centímetros"); unitCombo.Items.Add("Pulgadas"); unitCombo.Items.Add("Milímetros");
        unitCombo.SelectedIndex = _config.Unit switch { Units.In => 1, Units.Mm => 2, _ => 0 };
        string UnitOf() => unitCombo.SelectedIndex switch { 1 => Units.In, 2 => Units.Mm, _ => Units.Cm };

        var wBox = new TextBox { Text = _config.PageWidth.ToString("0.##", inv) };
        var hBox = new TextBox { Text = _config.PageHeight.ToString("0.##", inv) };
        var rbPortrait = new RadioButton { Content = "Vertical", GroupName = "ori", IsChecked = _config.PageHeight >= _config.PageWidth };
        var rbLandscape = new RadioButton { Content = "Horizontal", GroupName = "ori", IsChecked = _config.PageWidth > _config.PageHeight };
        var presetName = new TextBox { PlaceholderText = "Nombre del preset", HorizontalAlignment = HorizontalAlignment.Stretch };

        string curUnit = _config.Unit;
        void ApplyOrientation()
        {
            double w = ParseD(wBox.Text, 0), h = ParseD(hBox.Text, 0);
            if (rbLandscape.IsChecked == true && h > w) { wBox.Text = h.ToString("0.##", inv); hBox.Text = w.ToString("0.##", inv); }
            else if (rbPortrait.IsChecked == true && w > h) { wBox.Text = h.ToString("0.##", inv); hBox.Text = w.ToString("0.##", inv); }
        }
        rbPortrait.Checked += (_, _) => ApplyOrientation();
        rbLandscape.Checked += (_, _) => ApplyOrientation();

        presetCombo.SelectionChanged += (_, _) =>
        {
            if (presetCombo.SelectedItem is Preset p)
            {
                unitCombo.SelectedIndex = p.Unit switch { Units.In => 1, Units.Mm => 2, _ => 0 };
                curUnit = p.Unit;
                wBox.Text = p.Width.ToString("0.##", inv);
                hBox.Text = p.Height.ToString("0.##", inv);
                _pageName = p.Name;
            }
        };
        unitCombo.SelectionChanged += (_, _) =>
        {
            string nu = UnitOf();
            if (nu == curUnit) return;
            double w = ParseD(wBox.Text, 0), h = ParseD(hBox.Text, 0);
            wBox.Text = LayoutEngine.FromPx(LayoutEngine.ToPx(w, curUnit), nu).ToString("0.##", inv);
            hBox.Text = LayoutEngine.FromPx(LayoutEngine.ToPx(h, curUnit), nu).ToString("0.##", inv);
            curUnit = nu;
        };

        var delBtn = new Button { Content = "Eliminar preset" };
        delBtn.Click += async (_, _) =>
        {
            if (presetCombo.SelectedItem is Preset p && !p.Builtin)
            {
                if (await PresetService.DeleteAsync(p.Id)) { presets.Remove(p); presetCombo.ItemsSource = null; presetCombo.ItemsSource = presets; }
            }
        };
        var saveBtn = new Button { Content = "Guardar preset" };
        saveBtn.Click += async (_, _) =>
        {
            var name = presetName.Text.Trim();
            if (name.Length == 0) return;
            var np = await PresetService.CreateAsync(name, ParseD(wBox.Text, 1), ParseD(hBox.Text, 1), UnitOf());
            if (np is not null) { presets.Add(np); presetCombo.ItemsSource = null; presetCombo.ItemsSource = presets; presetCombo.SelectedItem = np; }
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = "Tamaño predefinido", FontSize = 12 });
        var presetRow = new Grid { ColumnSpacing = 6 };
        presetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        presetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(presetCombo, 0); Grid.SetColumn(delBtn, 1);
        presetRow.Children.Add(presetCombo); presetRow.Children.Add(delBtn);
        panel.Children.Add(presetRow);
        panel.Children.Add(new TextBlock { Text = "Unidad", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(unitCombo);
        var dimRow = new Grid { ColumnSpacing = 8 };
        dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var c0 = new StackPanel { Spacing = 3 }; c0.Children.Add(new TextBlock { Text = "Ancho", FontSize = 12 }); c0.Children.Add(wBox);
        var c1 = new StackPanel { Spacing = 3 }; c1.Children.Add(new TextBlock { Text = "Alto", FontSize = 12 }); c1.Children.Add(hBox);
        Grid.SetColumn(c0, 0); Grid.SetColumn(c1, 1); dimRow.Children.Add(c0); dimRow.Children.Add(c1);
        panel.Children.Add(new TextBlock { Text = "Dimensiones", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(dimRow);
        panel.Children.Add(new TextBlock { Text = "Orientación", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        var oriRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        oriRow.Children.Add(rbPortrait); oriRow.Children.Add(rbLandscape);
        panel.Children.Add(oriRow);
        panel.Children.Add(new TextBlock { Text = "Guardar como preset", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        var saveRow = new Grid { ColumnSpacing = 6 };
        saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(presetName, 0); Grid.SetColumn(saveBtn, 1);
        saveRow.Children.Add(presetName); saveRow.Children.Add(saveBtn);
        panel.Children.Add(saveRow);

        var dialog = new ContentDialog
        {
            Title = "Configuración de Página",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Aceptar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _config.Unit = UnitOf();
            _config.PageWidth = Math.Max(1, ParseD(wBox.Text, _config.PageWidth));
            _config.PageHeight = Math.Max(1, ParseD(hBox.Text, _config.PageHeight));
            if (presetCombo.SelectedItem is Preset sp) _pageName = sp.Name;
            else _pageName = "Personalizado";
            SettingsStore.Save(_config);
            UpdatePageSummary();
            PageCanvas.Invalidate();
        }
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

    private void OnPosterButton(object sender, RoutedEventArgs e)
    {
        PosterToggle.IsOn = !PosterToggle.IsOn; // dispara OnPosterToggled
    }

    private void OnPosterToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _posterEnabled = PosterToggle.IsOn;
        PosterOptions.Visibility = _posterEnabled ? Visibility.Visible : Visibility.Collapsed;
        PageCanvas.Invalidate();
    }

    private void OnPosterFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _posterCols = ParseI(PosterColsBox?.Text, 2, 1, 4);
        _posterRows = ParseI(PosterRowsBox?.Text, 2, 1, 4);
        PageCanvas.Invalidate();
    }

    private void OnMarginsToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        MarginsPanel.Visibility = MarginsToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (MarginsToggle.IsOn) ApplyMargins();
        else _config.MarginTop = _config.MarginRight = _config.MarginBottom = _config.MarginLeft = 0;
        SettingsStore.Save(_config);
        PageCanvas.Invalidate();
    }

    private void OnMarginFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyMargins();
        SettingsStore.Save(_config);
        PageCanvas.Invalidate();
    }

    private void ApplyMargins()
    {
        _config.MarginTop = Math.Max(0, ParseD(MarginTopBox?.Text, _config.MarginTop));
        _config.MarginBottom = Math.Max(0, ParseD(MarginBottomBox?.Text, _config.MarginBottom));
        _config.MarginLeft = Math.Max(0, ParseD(MarginLeftBox?.Text, _config.MarginLeft));
        _config.MarginRight = Math.Max(0, ParseD(MarginRightBox?.Text, _config.MarginRight));
    }

    // ---------- Defaults globales de imagen (panel izquierdo) ----------

    private void OnGlobalChanged(object sender, SelectionChangedEventArgs e) { if (_ready) ApplyGlobal(); }
    private void OnGlobalSlider(object sender, RangeBaseValueChangedEventArgs e) { if (_ready) ApplyGlobal(); }

    private void ApplyGlobal()
    {
        if (GShape.SelectedIndex >= 0) _global.Shape = (ImageShape)GShape.SelectedIndex;
        _global.BorderWidth = GBorder.Value;
        _global.CornerRadius = GRadius.Value;
        if (GShadow.SelectedIndex >= 0) _global.Shadow = (ShadowStrength)GShadow.SelectedIndex;
        if (GFit.SelectedIndex >= 0) _global.Fit = (FitMode)GFit.SelectedIndex;
        if (GAlignH.SelectedIndex >= 0) _global.AlignH = (AlignH)GAlignH.SelectedIndex;
        if (GAlignV.SelectedIndex >= 0) _global.AlignV = (AlignV)GAlignV.SelectedIndex;
        UpdateInspector();
        PageCanvas.Invalidate();
    }

    private void OnGlobalBorderColor(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_ready) return;
        _global.BorderColor = args.NewColor;
        if (GBorderSwatch is not null) GBorderSwatch.Background = new SolidColorBrush(args.NewColor);
        PageCanvas.Invalidate();
    }

    private void OnGlobalCellBg(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_ready) return;
        _global.CellBg = args.NewColor;
        if (GCellSwatch is not null) GCellSwatch.Background = new SolidColorBrush(args.NewColor);
        PageCanvas.Invalidate();
    }

    private void OnCutGuidesToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _global.CutGuides = CutGuidesToggle.IsOn;
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
        SettingsStore.Save(_config);
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
        InsFit.SelectedIndex = img.Fit is null ? 0 : (int)img.Fit.Value + 1;
        InsZoom.Value = img.Zoom * 100;
        InsShape.SelectedIndex = img.Shape is null ? 0 : (int)img.Shape.Value + 1;
        InsRadius.Value = img.EffCornerRadius(_global);
        InsBorder.Value = img.EffBorderWidth(_global);
        SetSwatch(img.EffBorderColor(_global));
        InsBorderPicker.Color = img.EffBorderColor(_global);
        InsBrightness.Value = img.Brightness * 100;
        InsContrast.Value = img.Contrast * 100;
        InsSaturation.Value = img.Saturation * 100;
        InsGrayscale.Value = img.Grayscale * 100;
        InsSepia.Value = img.Sepia * 100;
        InsHue.Value = img.Hue;
        InsBlur.Value = img.Blur;
        InsInvert.Value = img.Invert * 100;
        InsOpacity.Value = img.Opacity * 100;
        InsOffsetX.Value = img.OffsetX * 100;
        InsOffsetY.Value = img.OffsetY * 100;
        InsCaptionText.Text = img.CaptionText;
        InsCaptionPos.SelectedIndex = (int)img.CaptionPos;
        InsCaptionSource.SelectedIndex = 0;
        InsCaptionFont.SelectedIndex = CaptionFontIndex(img.CaptionFont);
        InsCaptionSize.Value = img.CaptionSize;
        if (InsCapColorSwatch is not null) InsCapColorSwatch.Background = new SolidColorBrush(img.CaptionColor);
        InsCapColorPicker.Color = img.CaptionColor;
        if (InsCapBgSwatch is not null) InsCapBgSwatch.Background = new SolidColorBrush(img.CaptionBg);
        InsCapBgPicker.Color = img.CaptionBg;
        InsRestoreBtn.Visibility = img.OriginalBitmap is not null ? Visibility.Visible : Visibility.Collapsed;
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
        ApplyToSelected(i => i.Fit = InsFit.SelectedIndex <= 0 ? (FitMode?)null : (FitMode)(InsFit.SelectedIndex - 1));
    }

    private void OnInsZoomChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => i.Zoom = InsZoom.Value / 100.0);
    }

    private void OnInsRotate(object sender, RoutedEventArgs e) => RotateSelected();

    private void OnInsRotateLeft(object sender, RoutedEventArgs e)
    {
        foreach (var img in _selected) img.RotationDeg = (img.RotationDeg + 270) % 360;
        if (_selected.Count > 0) PageCanvas.Invalidate();
    }

    private void OnInsOffsetChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => { i.OffsetX = InsOffsetX.Value / 100.0; i.OffsetY = InsOffsetY.Value / 100.0; });
    }

    private void OnInsDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnRestoreOriginal(object sender, RoutedEventArgs e)
    {
        var img = Primary;
        if (img?.OriginalBitmap is null) return;
        img.Bitmap = img.OriginalBitmap;
        img.SourceBytes = img.OriginalBytes;
        img.SourcePath = img.OriginalPath;
        img.OriginalBitmap = null; img.OriginalBytes = null; img.OriginalPath = null;
        UpdateInspector();
        PageCanvas.Invalidate();
    }

    private BgRemover? _bg;

    private async void OnRemoveBg(object sender, RoutedEventArgs e)
    {
        var img = Primary;
        if (img is null) return;
        try
        {
            await RemoveBgCoreAsync(img);
            UpdateInspector();
            PageCanvas.Invalidate();
        }
        catch (Exception ex)
        {
            await ShowInfo("Quitar fondo", ex.Message);
        }
    }

    private async Task<CanvasBitmap> RemoveBgCoreAsync(EditorImage img)
    {
        var full = img.Bitmap;
        int w = (int)full.SizeInPixels.Width, h = (int)full.SizeInPixels.Height;
        byte[] fullBgra = full.GetPixelBytes();

        byte[] in320;
        using (var rt = new CanvasRenderTarget(PageCanvas.Device, 320, 320, 96))
        {
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(Colors.Black);
                ds.DrawImage(full, new Rect(0, 0, 320, 320));
            }
            in320 = rt.GetPixelBytes();
        }

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "u2netp.onnx");
        _bg ??= new BgRemover(modelPath);
        byte[] outBgra = await Task.Run(() => _bg.Run(fullBgra, w, h, in320));

        var cut = CanvasBitmap.CreateFromBytes(PageCanvas.Device, outBgra, w, h,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Premultiplied);

        // Backup del original (una sola vez) para "Restaurar original".
        if (img.OriginalBitmap is null)
        {
            img.OriginalBitmap = img.Bitmap;
            img.OriginalBytes = img.SourceBytes;
            img.OriginalPath = img.SourcePath;
        }
        img.Bitmap = cut;

        // Guardar PNG con alfa para recargar en device-lost.
        using var ras = new InMemoryRandomAccessStream();
        await cut.SaveAsync(ras, CanvasBitmapFileFormat.Png);
        ras.Seek(0);
        using var net = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await net.CopyToAsync(ms);
        img.SourcePath = null;
        img.SourceBytes = ms.ToArray();

        return cut;
    }

    private void OnInsShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInspector || InsShape.SelectedIndex < 0) return;
        ApplyToSelected(i => i.Shape = InsShape.SelectedIndex <= 0 ? (ImageShape?)null : (ImageShape)(InsShape.SelectedIndex - 1));
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
            i.Hue = InsHue.Value;
            i.Blur = InsBlur.Value;
            i.Invert = InsInvert.Value / 100.0;
            i.Opacity = InsOpacity.Value / 100.0;
        });
    }

    private void OnInsResetFilters(object sender, RoutedEventArgs e)
    {
        ApplyToSelected(i =>
        {
            i.Brightness = 1; i.Contrast = 1; i.Saturation = 1; i.Grayscale = 0; i.Sepia = 0;
            i.Hue = 0; i.Blur = 0; i.Invert = 0; i.Opacity = 1;
        });
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

    private void OnInsCaptionStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInspector) return;
        var f = (InsCaptionFont.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Segoe UI";
        ApplyToSelected(i => i.CaptionFont = f);
    }

    private void OnInsCaptionSlider(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInspector) return;
        ApplyToSelected(i => i.CaptionSize = InsCaptionSize.Value);
    }

    private void OnInsCaptionColor(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingInspector) return;
        if (InsCapColorSwatch is not null) InsCapColorSwatch.Background = new SolidColorBrush(args.NewColor);
        ApplyToSelected(i => i.CaptionColor = args.NewColor);
    }

    private void OnInsCaptionBg(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingInspector) return;
        if (InsCapBgSwatch is not null) InsCapBgSwatch.Background = new SolidColorBrush(args.NewColor);
        ApplyToSelected(i => i.CaptionBg = args.NewColor);
    }

    private void OnInsCaptionSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInspector || InsCaptionSource.SelectedIndex <= 0) return;
        int idx = InsCaptionSource.SelectedIndex; // 1=filename, 2=number
        foreach (var img in _selected)
        {
            int n = _images.IndexOf(img) + 1;
            img.CaptionText = idx == 1
                ? (img.SourcePath is not null ? Path.GetFileNameWithoutExtension(img.SourcePath) : $"Imagen {n}")
                : n.ToString();
            if (img.CaptionPos == CaptionPosition.None) img.CaptionPos = CaptionPosition.Below;
        }
        var p = Primary;
        if (p is not null)
        {
            _syncingInspector = true;
            InsCaptionText.Text = p.CaptionText;
            InsCaptionPos.SelectedIndex = (int)p.CaptionPos;
            _syncingInspector = false;
        }
        PageCanvas.Invalidate();
    }

    private static int CaptionFontIndex(string font) => font switch
    {
        "Arial" => 1, "Georgia" => 2, "Courier New" => 3, "Times New Roman" => 4, "Verdana" => 5, "Impact" => 6, _ => 0,
    };

    // ---------- Impresión directa a la impresora elegida (System.Drawing.Printing) ----------

    private void PopulatePrinters()
    {
        try
        {
            string def = "";
            try { def = new System.Drawing.Printing.PrinterSettings().PrinterName; } catch { }
            PrinterCombo.Items.Clear();
            int sel = -1, i = 0;
            foreach (string name in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            {
                PrinterCombo.Items.Add(name);
                if (name == def) sel = i;
                i++;
            }
            if (PrinterCombo.Items.Count > 0) PrinterCombo.SelectedIndex = sel >= 0 ? sel : 0;
        }
        catch { }
    }

    private string? SelectedPrinter => PrinterCombo?.SelectedItem as string;

    private void OnPrinterConfig(object sender, RoutedEventArgs e)
    {
        var printer = SelectedPrinter;
        if (string.IsNullOrEmpty(printer)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rundll32.exe",
                $"printui.dll,PrintUIEntry /e /n \"{printer}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _ = ShowInfo("Impresora", ex.Message); }
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_images.Count == 0) return;
        var printer = SelectedPrinter;
        if (string.IsNullOrEmpty(printer)) { await ShowInfo("Imprimir", "No hay impresora seleccionada."); return; }
        try
        {
            int total = ComputePrintPageCount();
            // Render de cada página a bitmap 300 DPI; GDI los imprime directo a la impresora elegida.
            var pages = new List<System.Drawing.Bitmap>();
            for (int p = 1; p <= total; p++)
            {
                byte[] png = await RenderPageJpegAsync(p, 300);
                pages.Add(new System.Drawing.Bitmap(new MemoryStream(png)));
            }

            using var doc = new System.Drawing.Printing.PrintDocument { DocumentName = "Imprime+" };
            doc.PrinterSettings.PrinterName = printer;
            doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(0, 0, 0, 0);
            // Orientación: si la página es más ancha que alta, pedir al driver horizontal
            // (apaisado). Así la impresora detecta y rota el papel según la config de página.
            var plLayout = LayoutEngine.ComputeLayout(_config);
            doc.DefaultPageSettings.Landscape = plLayout.PageW > plLayout.PageH;
            int idx = 0;
            doc.PrintPage += (s, ev) =>
            {
                var bmp = pages[idx];
                var pb = ev.PageBounds; // 1/100"
                double sc = Math.Min((double)pb.Width / bmp.Width, (double)pb.Height / bmp.Height);
                int w = (int)(bmp.Width * sc), h = (int)(bmp.Height * sc);
                int x = pb.X + (pb.Width - w) / 2, y = pb.Y + (pb.Height - h) / 2;
                ev.Graphics!.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                ev.Graphics.DrawImage(bmp, new System.Drawing.Rectangle(x, y, w, h));
                idx++;
                ev.HasMorePages = idx < pages.Count;
            };
            doc.Print();
            foreach (var b in pages) b.Dispose();
        }
        catch (Exception ex) { await ShowInfo("Imprimir", "No se pudo imprimir: " + ex.Message); }
    }

    private int ComputePrintPageCount()
    {
        if (IsPoster) return PosterPageCount;
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

        if (IsPoster)
        {
            if (pageNumber < 1 || pageNumber > PosterPageCount) return;
            double sc = Math.Min(imageable.Width / layout.PageW, imageable.Height / layout.PageH);
            double oxp = imageable.X + (imageable.Width - layout.PageW * sc) / 2.0;
            double oyp = imageable.Y + (imageable.Height - layout.PageH * sc) / 2.0;
            DrawPosterTile(ds, Primary ?? _images[0], layout, pageNumber - 1, oxp, oyp, sc, 0);
            return;
        }

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
            ImageRenderer.Draw(ds, img, dest, scale, _global);
            if (_global.CutGuides) DrawCutMarks(ds, dest);
        }
    }

    // ---------- Auto-update (chequea versión y corre el instalador nuevo) ----------

    private sealed class UpdateInfoDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string Version { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("url")] public string Url { get; set; } = "";
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await http.GetStringAsync("https://imprime.utp.hn/winui/winui-latest.json");
            var info = System.Text.Json.JsonSerializer.Deserialize<UpdateInfoDto>(json);
            var current = typeof(MainPage).Assembly.GetName().Version ?? new Version(0, 0);
            if (info is null || !Version.TryParse(info.Version, out var server) || server <= current)
            {
                await ShowInfo("Actualizaciones", $"Ya tenés la última versión ({current.ToString(3)}).");
                return;
            }

            var ask = new ContentDialog
            {
                Title = "Actualización disponible",
                Content = $"Hay una versión nueva ({info.Version}). Tenés la {current.ToString(3)}.\n¿Descargar e instalar ahora?",
                PrimaryButtonText = "Descargar e instalar",
                CloseButtonText = "Ahora no",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

            var url = info.Url.StartsWith("http") ? info.Url : "https://imprime.utp.hn" + info.Url;
            var bytes = await http.GetByteArrayAsync(url);
            var tmp = Path.Combine(Path.GetTempPath(), "ImprimePlus-Setup.exe");
            await File.WriteAllBytesAsync(tmp, bytes);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tmp) { UseShellExecute = true });
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            await ShowInfo("Actualizaciones", "No se pudo verificar: " + ex.Message);
        }
    }

    private async Task ShowInfo(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // ---------- Export PDF (cada página a 300 DPI; reusa DrawPrintPage) ----------

    private async void OnExportPdf(object sender, RoutedEventArgs e)
    {
        if (_images.Count == 0) return;
        var picker = new FileSavePicker { SuggestedFileName = "Imprime+" };
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await ExportPdfAsync(file.Path, 300);
    }

    private async Task ExportPdfAsync(string path, double dpi)
    {
        var layout = LayoutEngine.ComputeLayout(_config);
        if (layout.PageW <= 0 || layout.PageH <= 0) return;
        int pages = ComputePrintPageCount();
        double wIn = layout.PageW / 96.0, hIn = layout.PageH / 96.0;

        using var doc = new PdfDocument();
        for (int p = 1; p <= pages; p++)
        {
            byte[] jpeg = await RenderPageJpegAsync(p, dpi);
            var page = doc.AddPage();
            page.Width = XUnit.FromInch(wIn);
            page.Height = XUnit.FromInch(hIn);
            using var gfx = XGraphics.FromPdfPage(page);
            using var imgStream = new MemoryStream(jpeg);
            var ximg = XImage.FromStream(imgStream);
            gfx.DrawImage(ximg, 0, 0, page.Width.Point, page.Height.Point);
            ximg.Dispose();
        }
        doc.Save(path);
    }

    /// <summary>Render de una página a JPEG (bytes) a la DPI dada, reusando DrawPrintPage.</summary>
    private async Task<byte[]> RenderPageJpegAsync(int pageNumber, double dpi)
    {
        var layout = LayoutEngine.ComputeLayout(_config);
        using var rt = new CanvasRenderTarget(PageCanvas.Device, (float)layout.PageW, (float)layout.PageH, (float)dpi);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            DrawPrintPage(ds, pageNumber, new Rect(0, 0, layout.PageW, layout.PageH));
        }
        using var ras = new InMemoryRandomAccessStream();
        await rt.SaveAsync(ras, CanvasBitmapFileFormat.Jpeg, 0.95f);
        ras.Seek(0);
        using var net = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await net.CopyToAsync(ms);
        return ms.ToArray();
    }
}
