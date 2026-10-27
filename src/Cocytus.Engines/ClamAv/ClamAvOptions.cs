using System.Globalization;
using System.Text;
using Cocytus.Core;

namespace Cocytus.Engines.ClamAv;

public sealed record ClamAvOptions(
    string ClamAvDir,
    string DatabaseDir,
    string ConfigDir,
    string TempDir,
    string LogsDir,
    CocytusSettings Settings)
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Grava a assinatura de teste do Cocytus na pasta de bases ao iniciar.</summary>
    public bool InstallTestSignature { get; init; } = true;

    /// <summary>Exige as bases oficiais (.cvd/.cld). Só os testes automáticos desligam, para usar uma base mínima.</summary>
    public bool RequireOfficialDatabase { get; init; } = true;

    public string ClamdExe => Path.Combine(ClamAvDir, "clamd.exe");

    public string FreshclamExe => Path.Combine(ClamAvDir, "freshclam.exe");

    public static ClamAvOptions FromPaths(CocytusPaths paths, CocytusSettings settings) =>
        new(paths.ClamAvDir, paths.ClamAvDatabaseDir, paths.ConfigDir, paths.TempDir, paths.LogsDir, settings);

    /// <summary>Monta o clamd.conf. O clamd só aceita conexões de 127.0.0.1 e recebe conteúdo por INSTREAM.</summary>
    public string BuildClamdConfig(int port)
    {
        var s = Settings;
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Gerado pelo Cocytus a cada inicialização do motor. Alterações manuais são sobrescritas.");
        sb.AppendLine(c, $"TCPSocket {port}");
        sb.AppendLine("TCPAddr 127.0.0.1");
        sb.AppendLine(c, $"DatabaseDirectory \"{DatabaseDir}\"");
        sb.AppendLine(c, $"TemporaryDirectory \"{TempDir}\"");
        sb.AppendLine(c, $"LogFile \"{Path.Combine(LogsDir, "clamd.log")}\"");
        sb.AppendLine("LogFileMaxSize 5M");
        sb.AppendLine("LogRotate yes");
        sb.AppendLine("LogTime yes");
        sb.AppendLine("LogClean no");
        sb.AppendLine("Foreground yes");
        sb.AppendLine(c, $"MaxThreads {Math.Max(2, s.Parallelism + 1)}");
        sb.AppendLine("MaxQueue 100");
        sb.AppendLine("IdleTimeout 60");
        sb.AppendLine(c, $"ReadTimeout {Math.Max(30, s.PerFileTimeoutSeconds)}");
        sb.AppendLine("CommandReadTimeout 30");
        sb.AppendLine("SelfCheck 600");
        sb.AppendLine(c, $"StreamMaxLength {s.MaxFileSizeMb + 1}M");
        sb.AppendLine(c, $"MaxScanSize {s.ArchiveMaxScanSizeMb}M");
        sb.AppendLine(c, $"MaxFileSize {s.ArchiveMaxFileSizeMb}M");
        sb.AppendLine(c, $"MaxRecursion {s.ArchiveMaxRecursion}");
        sb.AppendLine(c, $"MaxFiles {s.ArchiveMaxFiles}");
        sb.AppendLine(c, $"MaxScanTime {s.ArchiveMaxScanTimeSeconds * 1000}");
        sb.AppendLine("AlertExceedsMax yes");
        sb.AppendLine("HeuristicAlerts yes");
        sb.AppendLine(s.DetectPua ? "DetectPUA yes" : "DetectPUA no");
        sb.AppendLine("Bytecode yes");
        sb.AppendLine("BytecodeSecurity TrustSigned");
        return sb.ToString();
    }

    /// <summary>Texto mostrado junto dos resultados: o que este motor faz e o que não faz.</summary>
    public string DescribeLimitations()
    {
        var s = Settings;
        return "Motor: ClamAV (assinaturas, verificação de formatos e heurísticas próprias do ClamAV). "
            + "Não há emulação, análise de comportamento, proteção em tempo real nem reputação em nuvem. "
            + $"Arquivos acima de {s.MaxFileSizeMb} MB não são analisados. "
            + $"Compactados: até {s.ArchiveMaxScanSizeMb} MB extraídos, {s.ArchiveMaxFileSizeMb} MB por arquivo interno, "
            + $"{s.ArchiveMaxRecursion} níveis, {s.ArchiveMaxFiles} arquivos e {s.ArchiveMaxScanTimeSeconds} s por item. "
            + (s.DetectPua ? "Detecção de PUA ativada." : "Detecção de PUA desativada.");
    }
}
