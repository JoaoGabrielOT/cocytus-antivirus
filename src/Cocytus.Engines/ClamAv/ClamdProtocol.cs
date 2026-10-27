using System.Globalization;
using Cocytus.Core;

namespace Cocytus.Engines.ClamAv;

/// <summary>Interpretação das respostas do clamd (protocolo documentado em "man clamd").</summary>
public static class ClamdProtocol
{
    private const string LimitsPrefix = "Heuristics.Limits.Exceeded";

    /// <summary>
    /// Converte a resposta de INSTREAM em veredito. Exemplos:
    /// "stream: OK", "stream: Win.Test.EICAR_HDB-1 FOUND", "INSTREAM size limit exceeded. ERROR".
    /// </summary>
    public static EngineVerdict ParseScanResponse(string response)
    {
        var text = (response ?? string.Empty).Trim('\0', ' ', '\r', '\n');
        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var body = text[..^" FOUND".Length];
            var colon = body.IndexOf(": ", StringComparison.Ordinal);
            var name = (colon >= 0 ? body[(colon + 2)..] : body).Trim();
            return ClassifySignature(name);
        }

        if (text.EndsWith(" OK", StringComparison.Ordinal) || text == "OK")
        {
            return EngineVerdict.Clean;
        }

        if (text.EndsWith(" ERROR", StringComparison.Ordinal))
        {
            return new EngineVerdict(VerdictKind.Error, null, "O ClamAV recusou o conteúdo: " + text[..^" ERROR".Length].Trim());
        }

        return new EngineVerdict(VerdictKind.Error, null, "Resposta inesperada do clamd: " + (text.Length > 200 ? text[..200] : text));
    }

    /// <summary>Classifica um nome de assinatura do ClamAV nas categorias do Cocytus.</summary>
    public static EngineVerdict ClassifySignature(string name)
    {
        if (name.StartsWith(LimitsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var limit = name.Length > LimitsPrefix.Length + 1 ? name[(LimitsPrefix.Length + 1)..] : name;
            return new EngineVerdict(VerdictKind.LimitsExceeded, name,
                $"Limite do ClamAV excedido ({DescribeLimit(limit)}); o conteúdo não foi analisado por completo");
        }

        if (name.StartsWith("Heuristics.", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineVerdict(VerdictKind.Suspicious, name, "Detecção heurística do ClamAV (não é uma assinatura de ameaça conhecida)");
        }

        if (name.StartsWith("PUA.", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineVerdict(VerdictKind.Suspicious, name, "Aplicativo potencialmente indesejado (PUA); pode ser legítimo");
        }

        return new EngineVerdict(VerdictKind.Detected, name, "Assinatura do ClamAV");
    }

    private static string DescribeLimit(string limit) => limit switch
    {
        _ when limit.StartsWith("MaxFileSize", StringComparison.OrdinalIgnoreCase) => "tamanho de arquivo interno",
        _ when limit.StartsWith("MaxScanSize", StringComparison.OrdinalIgnoreCase) => "total de dados extraídos",
        _ when limit.StartsWith("MaxRecursion", StringComparison.OrdinalIgnoreCase) => "profundidade de compactados",
        _ when limit.StartsWith("MaxFiles", StringComparison.OrdinalIgnoreCase) => "quantidade de arquivos internos",
        _ when limit.StartsWith("MaxScanTime", StringComparison.OrdinalIgnoreCase) => "tempo de análise",
        _ => limit,
    };

    /// <summary>"ClamAV 1.5.4/28163/Fri Oct 23 07:23:45 2026" → versão do motor e da base daily.</summary>
    public static (string EngineVersion, string? DatabaseVersion) ParseVersion(string response)
    {
        var text = (response ?? string.Empty).Trim('\0', ' ', '\r', '\n');
        if (text.StartsWith("ClamAV ", StringComparison.Ordinal))
        {
            text = text["ClamAV ".Length..];
        }

        var parts = text.Split('/');
        var engine = parts[0].Trim();
        var database = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? parts[1]
            : null;
        return (engine, database);
    }
}
