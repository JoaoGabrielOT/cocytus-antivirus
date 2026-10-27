namespace Cocytus.Core;

/// <summary>Configurações do usuário. Os valores são normalizados por <see cref="Normalize"/>.</summary>
public sealed class CocytusSettings
{
    public const int MinFileSizeMb = 1;
    public const int MaxFileSizeMbLimit = 2000;

    /// <summary>Arquivos maiores que isto não são enviados ao motor e aparecem como "não analisado".</summary>
    public int MaxFileSizeMb { get; set; } = 200;

    /// <summary>Quantos arquivos são analisados ao mesmo tempo.</summary>
    public int Parallelism { get; set; } = 2;

    /// <summary>Tempo máximo de análise por arquivo, em segundos.</summary>
    public int PerFileTimeoutSeconds { get; set; } = 180;

    /// <summary>Limite total de dados extraídos de um compactado (MaxScanSize do ClamAV), em MB.</summary>
    public int ArchiveMaxScanSizeMb { get; set; } = 400;

    /// <summary>Tamanho máximo de cada arquivo dentro de um compactado (MaxFileSize do ClamAV), em MB.</summary>
    public int ArchiveMaxFileSizeMb { get; set; } = 200;

    /// <summary>Profundidade máxima de compactados dentro de compactados (MaxRecursion).</summary>
    public int ArchiveMaxRecursion { get; set; } = 16;

    /// <summary>Quantidade máxima de arquivos dentro de um compactado (MaxFiles).</summary>
    public int ArchiveMaxFiles { get; set; } = 10000;

    /// <summary>Tempo máximo que o ClamAV gasta num único conteúdo (MaxScanTime), em segundos.</summary>
    public int ArchiveMaxScanTimeSeconds { get; set; } = 120;

    /// <summary>Ativa as assinaturas PUA (aplicativos potencialmente indesejados) do ClamAV.</summary>
    public bool DetectPua { get; set; }

    /// <summary>Mantém o clamd carregado entre varreduras (usa cerca de 1 GB de RAM com as bases completas).</summary>
    public bool KeepEngineLoaded { get; set; } = true;

    /// <summary>Verificação em tempo real ligada.</summary>
    public bool RealTimeEnabled { get; set; } = true;

    /// <summary>Pastas vigiadas pelo tempo real. Nulo = pastas padrão do usuário (Downloads, Área de Trabalho, Documentos, Temp).</summary>
    public List<string>? RealTimeFolders { get; set; }

    /// <summary>Procura versões novas do Cocytus no GitHub ao abrir e a cada 24 horas.</summary>
    public bool CheckAppUpdates { get; set; } = true;

    /// <summary>Versão que o usuário escolheu pular (não volta a perguntar por ela).</summary>
    public string? SkippedAppVersion { get; set; }

    /// <summary>Última consulta de versão nova (automática ou manual).</summary>
    public DateTimeOffset? LastAppUpdateCheck { get; set; }

    public CocytusSettings Normalize()
    {
        if (RealTimeFolders is not null)
        {
            RealTimeFolders = RealTimeFolders
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        MaxFileSizeMb = Math.Clamp(MaxFileSizeMb, MinFileSizeMb, MaxFileSizeMbLimit);
        Parallelism = Math.Clamp(Parallelism, 1, 8);
        PerFileTimeoutSeconds = Math.Clamp(PerFileTimeoutSeconds, 10, 3600);
        ArchiveMaxScanSizeMb = Math.Clamp(ArchiveMaxScanSizeMb, 1, 4000);
        ArchiveMaxFileSizeMb = Math.Clamp(ArchiveMaxFileSizeMb, 1, 4000);
        ArchiveMaxRecursion = Math.Clamp(ArchiveMaxRecursion, 1, 64);
        ArchiveMaxFiles = Math.Clamp(ArchiveMaxFiles, 1, 1_000_000);
        ArchiveMaxScanTimeSeconds = Math.Clamp(ArchiveMaxScanTimeSeconds, 5, 3600);
        return this;
    }

    public CocytusSettings Clone()
    {
        var copy = (CocytusSettings)MemberwiseClone();
        copy.RealTimeFolders = RealTimeFolders?.ToList();
        return copy;
    }

    public bool RealTimeSettingsEqual(CocytusSettings other) =>
        RealTimeEnabled == other.RealTimeEnabled
        && MaxFileSizeMb == other.MaxFileSizeMb
        && PerFileTimeoutSeconds == other.PerFileTimeoutSeconds
        && (RealTimeFolders ?? []).SequenceEqual(other.RealTimeFolders ?? [], StringComparer.OrdinalIgnoreCase)
        && (RealTimeFolders is null) == (other.RealTimeFolders is null);

    public bool EngineSettingsEqual(CocytusSettings other) =>
        MaxFileSizeMb == other.MaxFileSizeMb
        && ArchiveMaxScanSizeMb == other.ArchiveMaxScanSizeMb
        && ArchiveMaxFileSizeMb == other.ArchiveMaxFileSizeMb
        && ArchiveMaxRecursion == other.ArchiveMaxRecursion
        && ArchiveMaxFiles == other.ArchiveMaxFiles
        && ArchiveMaxScanTimeSeconds == other.ArchiveMaxScanTimeSeconds
        && DetectPua == other.DetectPua;
}
