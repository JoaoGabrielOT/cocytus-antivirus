namespace Cocytus.Core;

/// <summary>Textos em português usados pela interface e pelo histórico.</summary>
public static class Texts
{
    public const string NoThreatDisclaimer =
        "\"Nenhuma ameaça detectada\" significa apenas que o ClamAV, com as bases instaladas, não reconheceu nada. "
        + "Não é garantia de que o arquivo seja seguro: o Cocytus não faz emulação, análise de comportamento nem consulta reputação em nuvem.";

    public static string Outcome(ScanOutcome outcome) => outcome switch
    {
        ScanOutcome.NoThreatDetected => "Nenhuma ameaça detectada",
        ScanOutcome.Threat => "Ameaça detectada",
        ScanOutcome.Suspicious => "Suspeito",
        ScanOutcome.NotScanned => "Não analisado",
        ScanOutcome.ReadError => "Erro de leitura",
        _ => outcome.ToString(),
    };

    public static string Reason(NotScannedReason reason) => reason switch
    {
        NotScannedReason.None => "",
        NotScannedReason.TooLarge => "Acima do limite de tamanho",
        NotScannedReason.ArchiveLimitsExceeded => "Limites de compactado excedidos",
        NotScannedReason.ReparsePoint => "Link simbólico ou junção",
        NotScannedReason.CloudPlaceholder => "Somente na nuvem",
        NotScannedReason.Timeout => "Tempo esgotado",
        NotScannedReason.EngineError => "Erro do motor",
        _ => reason.ToString(),
    };

    public static string Status(ScanStatus status) => status switch
    {
        ScanStatus.Completed => "Concluída",
        ScanStatus.Cancelled => "Cancelada",
        ScanStatus.Failed => "Falhou",
        _ => status.ToString(),
    };
}
