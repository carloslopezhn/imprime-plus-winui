using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ImprimePlus.Editor;

/// <summary>
/// Carga de imágenes a CanvasBitmap RESPETANDO la orientación EXIF y con gestión
/// de color a sRGB. CanvasBitmap.LoadAsync por sí solo NO auto-rota por EXIF, así
/// que decodificamos vía BitmapDecoder (que sí aplica la orientación) y creamos el
/// bitmap GPU desde el SoftwareBitmap resultante.
/// </summary>
public static class ImageLoader
{
    public static readonly string[] ImageExts =
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff", ".heic", ".heif" };

    public static bool IsImage(string nameOrPath)
    {
        var ext = Path.GetExtension(nameOrPath).ToLowerInvariant();
        return ImageExts.Contains(ext);
    }

    public static async Task<CanvasBitmap?> LoadFromFileAsync(ICanvasResourceCreator rc, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            return await DecodeAsync(rc, stream);
        }
        catch { return null; }
    }

    public static async Task<CanvasBitmap?> LoadFromBytesAsync(ICanvasResourceCreator rc, byte[] data)
    {
        try
        {
            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(data.AsBuffer());
            ras.Seek(0);
            return await DecodeAsync(rc, ras);
        }
        catch { return null; }
    }

    private static async Task<CanvasBitmap> DecodeAsync(ICanvasResourceCreator rc, IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var sb = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, // <- auto-rota por EXIF
            ColorManagementMode.ColorManageToSRgb);
        return CanvasBitmap.CreateFromSoftwareBitmap(rc, sb);
    }
}
