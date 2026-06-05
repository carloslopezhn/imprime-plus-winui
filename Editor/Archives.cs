using SharpCompress.Archives;

namespace ImprimePlus.Editor;

/// <summary>
/// Extracción de imágenes desde comprimidos (ZIP, RAR, 7Z, TAR y TAR.GZ/BZ2/XZ)
/// con SharpCompress. Maneja anidamiento (p.ej. tar.gz = gzip que contiene un tar)
/// recursando hasta una profundidad razonable.
/// </summary>
public static class Archives
{
    private static readonly string[] ArchiveExts =
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".lz" };

    public static bool IsArchive(string nameOrPath)
    {
        var ext = Path.GetExtension(nameOrPath).ToLowerInvariant();
        return ArchiveExts.Contains(ext);
    }

    /// <summary>Devuelve (nombre, bytes) de cada imagen contenida en el comprimido.</summary>
    public static List<(string Name, byte[] Data)> ExtractImages(string archivePath)
    {
        var result = new List<(string, byte[])>();
        try
        {
            using var fs = File.OpenRead(archivePath);
            Collect(fs, result, 0);
        }
        catch { /* comprimido corrupto o no soportado */ }
        return result;
    }

    private static void Collect(Stream stream, List<(string, byte[])> outp, int depth)
    {
        if (depth > 3) return;
        using var archive = ArchiveFactory.OpenArchive(stream); // 0.49: Open -> OpenArchive
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            var key = entry.Key ?? "";

            if (ImageLoader.IsImage(key))
            {
                using var ms = new MemoryStream();
                using (var es = entry.OpenEntryStream()) es.CopyTo(ms);
                outp.Add((key, ms.ToArray()));
            }
            else if (IsArchive(key))
            {
                // Comprimido anidado (p.ej. el .tar dentro de un .gz): recursar.
                using var ms = new MemoryStream();
                using (var es = entry.OpenEntryStream()) es.CopyTo(ms);
                ms.Position = 0;
                try { Collect(ms, outp, depth + 1); } catch { }
            }
        }
    }
}
