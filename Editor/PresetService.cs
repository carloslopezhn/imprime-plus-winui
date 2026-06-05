using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ImprimePlus.Editor;

public sealed class Preset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "cm";
    [JsonPropertyName("builtin")] public bool Builtin { get; set; }
    public override string ToString() => Name;
}

/// <summary>
/// Presets de tamaño de página. Usa la API Flask existente (/api/presets) y cae a
/// una lista builtin si no hay red. Espejo del comportamiento del Imprime+ viejo.
/// </summary>
public static class PresetService
{
    private const string Base = "https://imprime.utp.hn";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static List<Preset> Builtin() => new()
    {
        new() { Id = "letter", Name = "Carta", Width = 21.59, Height = 27.94, Unit = "cm", Builtin = true },
        // Oficio (Folio/F4): 8.5 x 13 pulg.  Legal (US Legal): 8.5 x 14 pulg. Son distintos.
        new() { Id = "oficio", Name = "Oficio", Width = 21.59, Height = 33.02, Unit = "cm", Builtin = true },
        new() { Id = "legal", Name = "Legal", Width = 21.59, Height = 35.56, Unit = "cm", Builtin = true },
        new() { Id = "a4", Name = "A4", Width = 21.0, Height = 29.7, Unit = "cm", Builtin = true },
        new() { Id = "a5", Name = "A5", Width = 14.8, Height = 21.0, Unit = "cm", Builtin = true },
        new() { Id = "4x6", Name = "4x6 pulg", Width = 10.16, Height = 15.24, Unit = "cm", Builtin = true },
        new() { Id = "5x7", Name = "5x7 pulg", Width = 12.7, Height = 17.78, Unit = "cm", Builtin = true },
    };

    public static async Task<List<Preset>> GetAsync()
    {
        try
        {
            var list = await Http.GetFromJsonAsync<List<Preset>>($"{Base}/api/presets");
            if (list is { Count: > 0 }) return list;
        }
        catch { }
        return Builtin();
    }

    public static async Task<Preset?> CreateAsync(string name, double width, double height, string unit)
    {
        try
        {
            var resp = await Http.PostAsJsonAsync($"{Base}/api/presets",
                new { name, width, height, unit });
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<Preset>();
        }
        catch { }
        return null;
    }

    public static async Task<bool> DeleteAsync(string id)
    {
        try
        {
            var resp = await Http.DeleteAsync($"{Base}/api/presets/{id}");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
