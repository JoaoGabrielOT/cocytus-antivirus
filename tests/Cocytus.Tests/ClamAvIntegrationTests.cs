using System.IO.Compression;
using System.Text;
using Cocytus.Core;
using Cocytus.Core.Scanning;
using Cocytus.Engines.ClamAv;

namespace Cocytus.Tests;

/// <summary>
/// Testes com o clamd real. Usam uma base mínima (só a assinatura de teste do Cocytus), que carrega em segundos,
/// exceto o teste do EICAR, que precisa das bases oficiais. Nenhum malware real é usado; o EICAR é montado em
/// memória e enviado ao clamd sem ser gravado em disco.
/// </summary>
[Collection("ClamAV")]
public sealed class ClamAvIntegrationTests
{
    private static ClamAvEngine CreateEngine(TempDir dir, CocytusSettings? settings = null, string? databaseDir = null)
    {
        var db = databaseDir ?? dir.Combine("bases");
        Directory.CreateDirectory(db);
        var options = new ClamAvOptions(TestEnvironment.ClamAvDir!, db, dir.Combine("config"), dir.Combine("tmp"), dir.Combine("logs"),
            (settings ?? new CocytusSettings()).Normalize())
        {
            RequireOfficialDatabase = databaseDir is not null,
            StartupTimeout = TimeSpan.FromMinutes(3),
        };
        return new ClamAvEngine(options);
    }

    [ClamAvFact]
    public async Task Detecta_marcador_de_teste_e_aceita_conteudo_comum()
    {
        using var dir = new TempDir();
        await using var engine = CreateEngine(dir);

        var info = await engine.EnsureReadyAsync(CancellationToken.None);
        Assert.Equal("ClamAV", info.Name);
        Assert.StartsWith("1.", info.Version, StringComparison.Ordinal);

        using var marked = new MemoryStream(Encoding.ASCII.GetBytes(ClamAvDatabase.TestMarker + " resto do arquivo"));
        var detected = await engine.ScanAsync(marked, CancellationToken.None);
        Assert.Equal(VerdictKind.Detected, detected.Kind);
        Assert.StartsWith(ClamAvDatabase.TestSignatureName, detected.SignatureName, StringComparison.Ordinal);

        using var clean = new MemoryStream(Encoding.UTF8.GetBytes("um texto qualquer, sem nada de especial"));
        Assert.Equal(VerdictKind.NoDetection, (await engine.ScanAsync(clean, CancellationToken.None)).Kind);

        using var empty = new MemoryStream();
        Assert.Equal(VerdictKind.NoDetection, (await engine.ScanAsync(empty, CancellationToken.None)).Kind);
    }

    [ClamAvFact]
    public async Task Compactado_acima_do_limite_vira_nao_analisado()
    {
        using var dir = new TempDir();
        await using var engine = CreateEngine(dir, new CocytusSettings { ArchiveMaxFileSizeMb = 1 });
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("grande.bin", CompressionLevel.Optimal);
            using var stream = entry.Open();
            var data = new byte[3 * 1024 * 1024];
            Random.Shared.NextBytes(data);
            stream.Write(data);
        }

        zip.Position = 0;
        var verdict = await engine.ScanAsync(zip, CancellationToken.None);

        Assert.Equal(VerdictKind.LimitsExceeded, verdict.Kind);
        Assert.StartsWith("Heuristics.Limits.Exceeded", verdict.SignatureName, StringComparison.Ordinal);
    }

    [ClamAvFact]
    public async Task Varredura_completa_com_motor_real_e_caminhos_acentuados()
    {
        using var dir = new TempDir();
        var target = dir.Combine("alvo com espaço");
        var threat = dir.WriteFile(Path.Combine("alvo com espaço", "relatório ação 文件.txt"), ClamAvDatabase.TestMarker);
        dir.WriteFile(Path.Combine("alvo com espaço", "sub", "comum.txt"), "nada a declarar");
        await using var engine = CreateEngine(dir);

        var report = await new ScanOrchestrator(engine, new ScanOptions(10 * 1024 * 1024, 2, TimeSpan.FromSeconds(60)))
            .RunAsync([target], null, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(1, report.Threats);
        Assert.Equal(1, report.NoThreat);
        Assert.Equal(threat, Assert.Single(report.Items).Path);
        Assert.NotNull(report.Engine);
    }

    [ClamAvFact]
    public async Task Parar_encerra_o_clamd_e_ele_volta_sob_demanda()
    {
        using var dir = new TempDir();
        await using var engine = CreateEngine(dir);
        await engine.EnsureReadyAsync(CancellationToken.None);
        Assert.True(engine.IsRunning);

        await engine.StopAsync();
        Assert.False(engine.IsRunning);

        using var clean = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(VerdictKind.NoDetection, (await engine.ScanAsync(clean, CancellationToken.None)).Kind);
        Assert.True(engine.IsRunning);
    }

    [ClamAvFact]
    public async Task Sem_bases_oficiais_o_motor_fica_indisponivel()
    {
        using var dir = new TempDir();
        var empty = dir.Combine("vazia");
        Directory.CreateDirectory(empty);
        await using var engine = CreateEngine(dir, databaseDir: empty);

        await Assert.ThrowsAsync<EngineUnavailableException>(() => engine.EnsureReadyAsync(CancellationToken.None));
    }

    [ClamAvOfficialFact]
    public async Task Detecta_eicar_em_memoria_com_as_bases_oficiais()
    {
        using var dir = new TempDir();
        await using var engine = CreateEngine(dir, databaseDir: TestEnvironment.OfficialDatabaseDir);

        // Arquivo de teste padrão EICAR, montado em memória (invertido e em base64 no código-fonte).
        var reversed = Encoding.ASCII.GetString(Convert.FromBase64String(
            "KkgrSCQhRUxJRi1UU0VULVNVUklWSVROQS1EUkFETkFUUy1SQUNJRSR9NylDQzcpXlAoNDVYWlBcNFtQQUAlUCFPNVg="));
        var eicar = new string(reversed.Reverse().ToArray());
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(eicar));

        var verdict = await engine.ScanAsync(stream, CancellationToken.None);

        Assert.Equal(VerdictKind.Detected, verdict.Kind);
        Assert.Contains("EICAR", verdict.SignatureName, StringComparison.OrdinalIgnoreCase);
    }
}

[CollectionDefinition("ClamAV", DisableParallelization = true)]
public sealed class ClamAvCollection
{
}
