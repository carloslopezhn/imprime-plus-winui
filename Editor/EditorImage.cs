using Microsoft.Graphics.Canvas;
using ImprimePlus.Core.Layout;

namespace ImprimePlus.Editor;

/// <summary>Cómo encaja la imagen en su celda.</summary>
public enum FitMode
{
    Cover,   // llena la celda recortando (default)
    Contain, // entra completa con letterbox
    Stretch, // deforma para llenar
}

/// <summary>
/// Una imagen en el editor: el bitmap cargado (GPU) + los ajustes de presentación.
/// Los overrides de cuadrícula (colSpan/rowSpan) viven en <see cref="Overrides"/>,
/// que es lo que consume el LayoutEngine para paginar/colocar.
/// </summary>
public sealed class EditorImage
{
    public string Id { get; }
    public string? SourcePath { get; init; }
    public byte[]? SourceBytes { get; init; }   // origen sin archivo (portapapeles, comprimido)
    public CanvasBitmap Bitmap { get; set; }

    // Overrides de layout (spans) — los lee el motor.
    public ImageOverrides Overrides { get; } = new();

    // Ajustes de presentación dentro de la celda (Fase 3 básicos; más en Fase 6).
    public FitMode Fit { get; set; } = FitMode.Cover;
    public double Zoom { get; set; } = 1.0;     // zoom interno
    public double OffsetX { get; set; } = 0;     // pan interno (-1..1 relativo)
    public double OffsetY { get; set; } = 0;
    public double RotationDeg { get; set; } = 0; // múltiplos de 90 por ahora

    public EditorImage(string id, CanvasBitmap bitmap)
    {
        Id = id;
        Bitmap = bitmap;
    }

    /// <summary>Proyección al modelo de dominio que entiende el LayoutEngine.</summary>
    public ImageItem ToItem() => new() { Id = Id, SourcePath = SourcePath, Overrides = Overrides };
}
