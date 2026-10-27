using System.Globalization;
using System.Text;

namespace Cocytus.Engines.ClamAv;

/// <summary>Informações do cabeçalho de uma base oficial (.cvd ou .cld).</summary>
public sealed record ClamAvDatabaseFile(string Name, string FileName, int Version, int Signatures, int FunctionalityLevel, DateTimeOffset? BuildTime);

/// <summary>Leitura do cabeçalho de 512 bytes das bases CVD/CLD (sem carregar o motor).</summary>
public static class ClamAvDatabase
{
    private static readonly string[] Extensions = [".cvd", ".cld"];

    /// <summary>Assinatura própria, inofensiva, para testes manuais e automáticos (não é malware).</summary>
    public const string TestSignatureFile = "cocytus-teste.ndb";

    public const string TestSignatureName = "Cocytus.ArquivoDeTeste";

    /// <summary>Conteúdo que um arquivo precisa ter no início para ser detectado pela assinatura de teste.</summary>
    public const string TestMarker = "COCYTUS-ARQUIVO-DE-TESTE-INOFENSIVO";

    public static string TestSignatureLine =>
        $"{TestSignatureName}:0:0:{Convert.ToHexStringLower(Encoding.ASCII.GetBytes(TestMarker))}";

    /// <summary>Grava (se faltar ou estiver diferente) a assinatura de teste na pasta de bases.</summary>
    public static void EnsureTestSignature(string databaseDir)
    {
        Directory.CreateDirectory(databaseDir);
        var path = Path.Combine(databaseDir, TestSignatureFile);
        var content = TestSignatureLine + "\n";
        if (!File.Exists(path) || File.ReadAllText(path) != content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }

    public static bool HasOfficialDatabase(string databaseDir) =>
        Directory.Exists(databaseDir)
        && Directory.EnumerateFiles(databaseDir).Any(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<ClamAvDatabaseFile> ReadAll(string databaseDir)
    {
        if (!Directory.Exists(databaseDir))
        {
            return [];
        }

        var result = new List<ClamAvDatabaseFile>();
        foreach (var file in Directory.EnumerateFiles(databaseDir)
                     .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var header = TryReadHeader(file);
            if (header is not null)
            {
                result.Add(header);
            }
        }

        return result;
    }

    public static ClamAvDatabaseFile? TryReadHeader(string path)
    {
        try
        {
            var buffer = new byte[512];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ParseHeader(Path.GetFileName(path), Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Formato: "ClamAV-VDB:24 Oct 2026 07-23 +0000:28163:355788:90:md5:dsig:builder:stime".</summary>
    public static ClamAvDatabaseFile? ParseHeader(string fileName, string header)
    {
        var fields = header.TrimEnd('\0', ' ').Split(':');
        if (fields.Length < 5 || fields[0] != "ClamAV-VDB")
        {
            return null;
        }

        if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var signatures)
            || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var level))
        {
            return null;
        }

        return new ClamAvDatabaseFile(Path.GetFileNameWithoutExtension(fileName), fileName, version, signatures, level, ParseBuildTime(fields[1]));
    }

    internal static DateTimeOffset? ParseBuildTime(string value)
    {
        // "24 Oct 2026 07-23 +0000"
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            return null;
        }

        if (!DateTime.TryParseExact($"{parts[0]} {parts[1]} {parts[2]} {parts[3]}", "d MMM yyyy HH-mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var zone = parts[4];
        if (zone.Length != 5 || (zone[0] != '+' && zone[0] != '-')
            || !int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(zone.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        var offset = new TimeSpan(hours, minutes, 0);
        return new DateTimeOffset(local, zone[0] == '-' ? -offset : offset);
    }
}
