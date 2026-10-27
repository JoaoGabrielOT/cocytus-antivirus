using System.Security.Cryptography;
using System.Text;
using Cocytus.Core;
using Cocytus.Core.Scanning;

namespace Cocytus.Tests;

public sealed class ScanOrchestratorTests
{
    private static ScanOptions Options(long maxBytes = 1024 * 1024, int parallelism = 2, double timeoutSeconds = 30) =>
        new(maxBytes, parallelism, TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task Detecta_ameaca_suspeito_e_conta_itens_sem_deteccao()
    {
        using var dir = new TempDir();
        dir.WriteFile("limpo1.txt", "conteúdo comum");
        dir.WriteFile("sub/limpo2.txt", "outro conteúdo");
        var threat = dir.WriteFile("sub/ameaca.txt", "xx MALWARE-FALSO xx");
        dir.WriteFile("sub/sub2/suspeito.txt", "SUSPEITO-FALSO");

        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([dir.Path], null, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(2, report.NoThreat);
        Assert.Equal(1, report.Threats);
        Assert.Equal(1, report.Suspicious);
        Assert.Equal(0, report.ReadErrors);
        Assert.Equal(4, report.Processed);
        var item = Assert.Single(report.Items, i => i.Outcome == ScanOutcome.Threat);
        Assert.Equal(threat, item.Path);
        Assert.Equal("Teste.MalwareFalso", item.ThreatName);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(threat))), item.Sha256);
        Assert.DoesNotContain(report.Items, i => i.Outcome == ScanOutcome.NoThreatDetected);
    }

    [Fact]
    public async Task Caminhos_com_espacos_e_acentos()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile(Path.Combine("pasta com espaço", "ação çãõ ñ 文件", "relatório final é.txt"), "MALWARE-FALSO");
        var clean = dir.WriteFile(Path.Combine("pasta com espaço", "arquivo comum ü.txt"), "nada");

        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([Path.Combine(dir.Path, "pasta com espaço")], null, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(1, report.Threats);
        Assert.Equal(1, report.NoThreat);
        Assert.Equal(path, Assert.Single(report.Items).Path);

        var single = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([clean], null, CancellationToken.None);
        Assert.Equal(1, single.NoThreat);
    }

    [Fact]
    public async Task Arquivo_acima_do_limite_nao_e_analisado()
    {
        using var dir = new TempDir();
        var big = dir.WriteBytes("grande.bin", new byte[4096]);
        var engine = new FakeEngine();

        var report = await new ScanOrchestrator(engine, Options(maxBytes: 1000)).RunAsync([dir.Path], null, CancellationToken.None);

        var item = Assert.Single(report.Items);
        Assert.Equal(big, item.Path);
        Assert.Equal(ScanOutcome.NotScanned, item.Outcome);
        Assert.Equal(NotScannedReason.TooLarge, item.Reason);
        Assert.Equal(0, engine.ScanCalls);
        Assert.Equal(0, report.Analyzed);
    }

    [Fact]
    public async Task Arquivo_bloqueado_vira_erro_de_leitura_sem_encerrar_a_varredura()
    {
        using var dir = new TempDir();
        var locked = dir.WriteFile("bloqueado.txt", "MALWARE-FALSO");
        dir.WriteFile("normal.txt", "ok");

        ScanReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([dir.Path], null, CancellationToken.None);
        }

        Assert.Equal(ScanStatus.Completed, report.Status);
        Assert.Equal(1, report.ReadErrors);
        Assert.Equal(1, report.NoThreat);
        var item = Assert.Single(report.Items);
        Assert.Equal(ScanOutcome.ReadError, item.Outcome);
        Assert.Contains("em uso", item.Detail, StringComparison.Ordinal);
        Assert.Equal(1, report.Analyzed);
    }

    [Fact]
    public async Task Alvo_inexistente_vira_erro_de_leitura()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "não existe.txt");

        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([missing], null, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, report.Status);
        var item = Assert.Single(report.Items);
        Assert.Equal(ScanOutcome.ReadError, item.Outcome);
        Assert.Equal(0, report.Analyzed);
    }

    [Fact]
    public async Task Juncao_nao_e_seguida()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "alvo");
        dir.WriteFile(Path.Combine("alvo", "dentro.txt"), "MALWARE-FALSO");
        var scanRoot = Path.Combine(dir.Path, "raiz");
        Directory.CreateDirectory(scanRoot);
        var link = Path.Combine(scanRoot, "link para alvo");
        Assert.True(TestEnvironment.TryCreateJunction(link, target), "não foi possível criar a junção de teste");

        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([scanRoot], null, CancellationToken.None);

        Assert.Equal(0, report.Threats);
        var item = Assert.Single(report.Items);
        Assert.Equal(ScanOutcome.NotScanned, item.Outcome);
        Assert.Equal(NotScannedReason.ReparsePoint, item.Reason);
        Directory.Delete(link);
    }

    [Fact]
    public async Task Cancelamento_interrompe_e_marca_como_cancelada()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 50; i++)
        {
            dir.WriteFile($"arquivo{i:00}.txt", i == 0 ? "LENTO" : "LENTO comum");
        }

        var engine = new FakeEngine();
        using var cts = new CancellationTokenSource();
        var run = new ScanOrchestrator(engine, Options(parallelism: 2, timeoutSeconds: 600)).RunAsync([dir.Path], null, cts.Token);
        await engine.SlowScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        var report = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanStatus.Cancelled, report.Status);
        Assert.True(report.Processed < 50);
        Assert.Equal(0, report.NoThreat);
    }

    [Fact]
    public async Task Tempo_esgotado_por_arquivo()
    {
        using var dir = new TempDir();
        dir.WriteFile("lento.txt", "LENTO");

        var report = await new ScanOrchestrator(new FakeEngine(), Options(timeoutSeconds: 0.3)).RunAsync([dir.Path], null, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, report.Status);
        var item = Assert.Single(report.Items);
        Assert.Equal(NotScannedReason.Timeout, item.Reason);
    }

    [Fact]
    public async Task Motor_indisponivel_falha_a_varredura()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.txt", "x");

        var report = await new ScanOrchestrator(new FakeEngine { Unavailable = true }, Options()).RunAsync([dir.Path], null, CancellationToken.None);

        Assert.Equal(ScanStatus.Failed, report.Status);
        Assert.Contains("motor de teste desligado", report.Error, StringComparison.Ordinal);
        Assert.Equal(0, report.Processed);
    }

    [Fact]
    public async Task Erro_do_motor_e_limites_viram_nao_analisado()
    {
        using var dir = new TempDir();
        dir.WriteFile("erro.txt", "ERRO-MOTOR");
        dir.WriteFile("zip.txt", "LIMITE-FALSO");

        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([dir.Path], null, CancellationToken.None);

        Assert.Equal(2, report.NotScanned);
        Assert.Contains(report.Items, i => i.Reason == NotScannedReason.EngineError);
        Assert.Contains(report.Items, i => i.Reason == NotScannedReason.ArchiveLimitsExceeded);
        Assert.Equal(0, report.Analyzed);
    }

    [Fact]
    public async Task Progresso_e_relatado()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 5; i++)
        {
            dir.WriteFile($"f{i}.txt", "x");
        }

        var phases = new List<ScanPhase>();
        var progress = new SyncProgress(p =>
        {
            lock (phases)
            {
                phases.Add(p.Phase);
            }
        });
        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync([dir.Path], progress, CancellationToken.None);

        Assert.Equal(5, report.NoThreat);
        Assert.Equal(ScanPhase.StartingEngine, phases[0]);
        Assert.Equal(ScanPhase.Finished, phases[^1]);
    }

    [Fact]
    public void Alvos_repetidos_ou_contidos_sao_removidos()
    {
        using var dir = new TempDir();
        var sub = Path.Combine(dir.Path, "sub");
        Directory.CreateDirectory(sub);
        var file = dir.WriteFile(Path.Combine("sub", "a.txt"), "x");

        var targets = FileEnumerator.NormalizeTargets([dir.Path, sub, file, dir.Path + "\\", dir.Path.ToUpperInvariant()]);

        Assert.Equal(new[] { dir.Path }, targets);
    }

    [Fact]
    public void Formata_tamanhos()
    {
        Assert.Equal("512 B", ScanOrchestrator.FormatBytes(512));
        Assert.Equal("1,5 KB", ScanOrchestrator.FormatBytes(1536).Replace('.', ','));
    }

    private sealed class SyncProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => action(value);
    }
}
