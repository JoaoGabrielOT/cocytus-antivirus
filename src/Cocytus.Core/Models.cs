namespace Cocytus.Core;

/// <summary>Classificação final de um item da varredura.</summary>
public enum ScanOutcome
{
    /// <summary>O motor analisou o conteúdo e não detectou ameaça. Não é garantia de que o arquivo seja seguro.</summary>
    NoThreatDetected,

    /// <summary>Assinatura de ameaça reconhecida pelo motor.</summary>
    Threat,

    /// <summary>Detecção heurística ou de aplicativo potencialmente indesejado.</summary>
    Suspicious,

    /// <summary>O conteúdo não foi analisado (ou não por completo). O motivo fica em <see cref="NotScannedReason"/>.</summary>
    NotScanned,

    /// <summary>Não foi possível ler o arquivo ou a pasta.</summary>
    ReadError,
}

public enum NotScannedReason
{
    None,
    TooLarge,
    ArchiveLimitsExceeded,
    ReparsePoint,
    CloudPlaceholder,
    Timeout,
    EngineError,
}

public enum VerdictKind
{
    NoDetection,
    Detected,
    Suspicious,
    LimitsExceeded,
    Error,
}

/// <summary>Resposta do motor para um conteúdo.</summary>
public sealed record EngineVerdict(VerdictKind Kind, string? SignatureName, string? Detail)
{
    public static EngineVerdict Clean { get; } = new(VerdictKind.NoDetection, null, null);
}

/// <summary>Identificação do motor e das bases usadas numa varredura.</summary>
public sealed record EngineInfo(
    string Name,
    string Version,
    string? DatabaseVersion,
    DateTimeOffset? DatabaseDate,
    string Limitations);

public sealed record FileScanResult(
    string Path,
    ScanOutcome Outcome,
    NotScannedReason Reason,
    string? ThreatName,
    string? Detail,
    long? Size,
    string? Sha256);

public enum ScanStatus
{
    Completed,
    Cancelled,
    Failed,
}

public enum ScanPhase
{
    StartingEngine,
    Scanning,
    Finished,
}

public sealed record ScanProgress(
    ScanPhase Phase,
    int Found,
    int Processed,
    int NoThreat,
    int Threats,
    int Suspicious,
    int NotScanned,
    int ReadErrors,
    long BytesScanned,
    string? CurrentPath);

/// <summary>Resultado consolidado de uma varredura. Itens sem detecção entram só na contagem.</summary>
public sealed record ScanReport(
    Guid Id,
    IReadOnlyList<string> Targets,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ScanStatus Status,
    string? Error,
    EngineInfo? Engine,
    int NoThreat,
    int Threats,
    int Suspicious,
    int NotScanned,
    int ReadErrors,
    long BytesScanned,
    IReadOnlyList<FileScanResult> Items,
    bool ItemsTruncated)
{
    public int Processed => NoThreat + Threats + Suspicious + NotScanned + ReadErrors;

    public Scanning.ScanKind Kind { get; init; } = Scanning.ScanKind.Selected;

    /// <summary>Arquivos cujo conteúdo foi de fato analisado pelo motor (sem falhas de leitura nem itens pulados).</summary>
    public int Analyzed => NoThreat + Threats + Suspicious;
}
