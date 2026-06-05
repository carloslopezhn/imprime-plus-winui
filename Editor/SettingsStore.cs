using System.Text.Json;
using ImprimePlus.Core.Layout;

namespace ImprimePlus.Editor;

/// <summary>
/// Persistencia de la configuración global en %LocalAppData%\ImprimePlus\settings.json.
/// (Unpackaged: ApplicationData.Current no es fiable sin identidad de paquete, así que
/// usamos una carpeta propia en LocalApplicationData.)
/// </summary>
public static class SettingsStore
{
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImprimePlus");

    private static string SettingsPath => Path.Combine(Dir, "settings.json");

    public static void Save(LayoutConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(config));
        }
        catch { /* disco lleno / permisos: no es crítico */ }
    }

    public static LayoutConfig? Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<LayoutConfig>(File.ReadAllText(SettingsPath));
        }
        catch { }
        return null;
    }
}
