using Cocytus.Core;
using Cocytus.Engines.ClamAv;
using Cocytus.Update;

namespace Cocytus.Tests;

public sealed class ClamAvParsingTests
{
    [Theory]
    [InlineData("stream: OK", VerdictKind.NoDetection, null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", VerdictKind.Detected, "Win.Test.EICAR_HDB-1")]
    [InlineData("stream: Cocytus.ArquivoDeTeste.UNOFFICIAL FOUND\0", VerdictKind.Detected, "Cocytus.ArquivoDeTeste.UNOFFICIAL")]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxFileSize FOUND", VerdictKind.LimitsExceeded, "Heuristics.Limits.Exceeded.MaxFileSize")]
    [InlineData("stream: Heuristics.Encrypted.Zip FOUND", VerdictKind.Suspicious, "Heuristics.Encrypted.Zip")]
    [InlineData("stream: PUA.Win.Tool.Exemplo FOUND", VerdictKind.Suspicious, "PUA.Win.Tool.Exemplo")]
    [InlineData("INSTREAM size limit exceeded. ERROR", VerdictKind.Error, null)]
    [InlineData("algo estranho", VerdictKind.Error, null)]
    [InlineData("", VerdictKind.Error, null)]
    public void Interpreta_respostas_do_clamd(string response, VerdictKind kind, string? name)
    {
        var verdict = ClamdProtocol.ParseScanResponse(response);

        Assert.Equal(kind, verdict.Kind);
        Assert.Equal(name, verdict.SignatureName);
    }

    [Fact]
    public void Interpreta_versao()
    {
        var (engine, db) = ClamdProtocol.ParseVersion("ClamAV 1.5.4/28163/Fri Oct 23 07:23:45 2026\0");

        Assert.Equal("1.5.4", engine);
        Assert.Equal("28163", db);
        Assert.Equal(("1.5.4", (string?)null), ClamdProtocol.ParseVersion("ClamAV 1.5.4"));
    }

    [Fact]
    public void Interpreta_cabecalho_cvd()
    {
        var header = "ClamAV-VDB:24 Oct 2026 07-23 +0000:28163:355788:90:abc:def:svc.clamav-publisher:1792815780".PadRight(512, ' ');

        var file = ClamAvDatabase.ParseHeader("daily.cvd", header);

        Assert.NotNull(file);
        Assert.Equal("daily", file.Name);
        Assert.Equal(28163, file.Version);
        Assert.Equal(355788, file.Signatures);
        Assert.Equal(90, file.FunctionalityLevel);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 7, 23, 0, TimeSpan.Zero), file.BuildTime);
        Assert.Null(ClamAvDatabase.ParseHeader("x.cvd", "não é uma base"));
    }

    [Fact]
    public void Assinatura_de_teste_corresponde_ao_marcador()
    {
        var line = ClamAvDatabase.TestSignatureLine;

        Assert.StartsWith("Cocytus.ArquivoDeTeste:0:0:", line, StringComparison.Ordinal);
        var hex = line.Split(':')[3];
        Assert.Equal(ClamAvDatabase.TestMarker, System.Text.Encoding.ASCII.GetString(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Configuracao_do_clamd_reflete_os_limites()
    {
        var settings = new CocytusSettings { MaxFileSizeMb = 50, ArchiveMaxRecursion = 7, DetectPua = true };
        var options = new ClamAvOptions(@"C:\motor", @"D:\dados com espaço\bases", @"D:\c", @"D:\t", @"D:\l", settings);

        var config = options.BuildClamdConfig(41234);

        Assert.Contains("TCPSocket 41234", config, StringComparison.Ordinal);
        Assert.Contains("TCPAddr 127.0.0.1", config, StringComparison.Ordinal);
        Assert.Contains("DatabaseDirectory \"D:\\dados com espaço\\bases\"", config, StringComparison.Ordinal);
        Assert.Contains("StreamMaxLength 51M", config, StringComparison.Ordinal);
        Assert.Contains("MaxRecursion 7", config, StringComparison.Ordinal);
        Assert.Contains("AlertExceedsMax yes", config, StringComparison.Ordinal);
        Assert.Contains("DetectPUA yes", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Mudanca_nas_bases_e_detectada_pelas_versoes()
    {
        var antes = new[] { new ClamAvDatabaseFile("daily", "daily.cvd", 28163, 1, 90, null), new ClamAvDatabaseFile("main", "main.cvd", 63, 1, 90, null) };
        var igual = new[] { new ClamAvDatabaseFile("main", "main.cvd", 63, 1, 90, null), new ClamAvDatabaseFile("daily", "daily.cvd", 28163, 1, 90, null) };
        var nova = new[] { new ClamAvDatabaseFile("daily", "daily.cld", 28164, 1, 90, null), new ClamAvDatabaseFile("main", "main.cvd", 63, 1, 90, null) };

        Assert.Equal(FreshclamUpdater.Fingerprint(antes), FreshclamUpdater.Fingerprint(igual));
        Assert.NotEqual(FreshclamUpdater.Fingerprint(antes), FreshclamUpdater.Fingerprint(nova));
    }

    [Theory]
    [InlineData(52, "rede")]
    [InlineData(54, "assinatura digital")]
    [InlineData(99, "desconhecido")]
    public void Descreve_falhas_do_freshclam(int code, string expected)
    {
        Assert.Contains(expected, FreshclamUpdater.DescribeFailure(code, ""), StringComparison.Ordinal);
        Assert.Contains("aguardar", FreshclamUpdater.DescribeFailure(code, "HTTP 429 cool-down"), StringComparison.Ordinal);
    }
}
