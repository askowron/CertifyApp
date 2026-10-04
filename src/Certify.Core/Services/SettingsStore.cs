using System.Text.Json;
using Certify.Core.Models;

namespace Certify.Core.Services;

/// <summary>
/// Persistence ustawien globalnych (appsettings.json w DataDirectory).
/// Brak pliku = domyslne. Nieznane pola ignorowane.
/// </summary>
public class SettingsStore
{
    private readonly AppSettings _defaults;
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public SettingsStore(AppSettings defaults) => _defaults = defaults;

    public string Path => _defaults.SettingsJsonPath;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Opts);
                if (loaded != null)
                {
                    // DataDirectory zawsze z domyslnych (nie nadpisujemy sciezki pliku samym plikiem).
                    loaded.DataDirectory = _defaults.DataDirectory;
                    return loaded;
                }
            }
        }
        catch { }
        return _defaults;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Opts));
    }
}
