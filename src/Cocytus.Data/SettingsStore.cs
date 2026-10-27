using System.Text.Json;
using Cocytus.Core;

namespace Cocytus.Data;

/// <summary>Configurações em JSON. Arquivo ausente ou corrompido volta aos padrões (e o problema é informado).</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    /// <summary>Carrega as configurações. <paramref name="warning"/> descreve problema de leitura, se houver.</summary>
    public CocytusSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(_path))
        {
            return new CocytusSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CocytusSettings>(File.ReadAllText(_path), AtomicFile.Json);
            if (settings is null)
            {
                warning = "Arquivo de configurações vazio; usando os valores padrão.";
                return new CocytusSettings();
            }

            return settings.Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = $"Não foi possível ler as configurações ({ex.Message}); usando os valores padrão.";
            return new CocytusSettings();
        }
    }

    public void Save(CocytusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicFile.WriteJson(_path, settings.Clone().Normalize());
    }
}
