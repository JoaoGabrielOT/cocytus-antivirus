using System.Text;
using Cocytus.Core;
using Cocytus.Core.Protection;
using Cocytus.Core.Scanning;

namespace Cocytus.Tests;

public sealed class RealTimeMonitorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static RealTimeMonitor Create(FakeEngine engine, string folder, params string[] excluded) =>
        new(_ => Task.FromResult<IScanEngine>(engine),
            new RealTimeOptions([folder], excluded, new ScanOptions(10 * 1024 * 1024, 1, TimeSpan.FromSeconds(30)))
            {
                SettleDelay = TimeSpan.FromMilliseconds(200),
                EngineRetryDelay = TimeSpan.FromMilliseconds(200),
            });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Tempo esgotado esperando a verificação em tempo real.");
            }

            await Task.Delay(50);
        }
    }

    private static async Task<RealTimeMonitor> StartAsync(RealTimeMonitor monitor)
    {
        monitor.Start();
        await WaitUntilAsync(() => monitor.Snapshot.State == RealTimeState.Active);
        return monitor;
    }

    [Fact]
    public async Task Arquivo_novo_com_ameaca_e_detectado_e_limpo_e_contado()
    {
        using var dir = new TempDir();
        var engine = new FakeEngine();
        await using var monitor = await StartAsync(Create(engine, dir.Path));
        var detections = new List<FileScanResult>();
        monitor.Detected += (_, r) =>
        {
            lock (detections)
            {
                detections.Add(r);
            }
        };

        dir.WriteFile("limpo.txt", "nada");
        var threat = dir.WriteFile(Path.Combine("sub pasta ç", "baixado.exe"), "MALWARE-FALSO");

        await WaitUntilAsync(() => monitor.Snapshot.Detections == 1 && monitor.Snapshot.Scanned >= 2);
        var hit = Assert.Single(detections);
        Assert.Equal(threat, hit.Path);
        Assert.Equal(ScanOutcome.Threat, hit.Outcome);
        Assert.NotNull(hit.Sha256);
    }

    [Fact]
    public async Task Pasta_excluida_e_download_parcial_sao_ignorados_e_o_renomeado_e_verificado()
    {
        using var dir = new TempDir();
        var excluded = dir.Combine("dados do cocytus");
        Directory.CreateDirectory(excluded);
        var engine = new FakeEngine();
        await using var monitor = await StartAsync(Create(engine, dir.Path, excluded));

        dir.WriteFile(Path.Combine("dados do cocytus", "x.txt"), "MALWARE-FALSO");
        var partial = dir.WriteFile("instalador.exe.crdownload", "MALWARE-FALSO");
        await Task.Delay(1500);
        Assert.Equal(0, monitor.Snapshot.Detections);

        File.Move(partial, dir.Combine("instalador.exe"));
        await WaitUntilAsync(() => monitor.Snapshot.Detections == 1);
    }

    [Fact]
    public async Task Arquivo_em_uso_e_verificado_quando_liberado()
    {
        using var dir = new TempDir();
        var engine = new FakeEngine();
        await using var monitor = await StartAsync(Create(engine, dir.Path));

        var path = dir.Combine("gravando.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.ASCII.GetBytes("MALWARE-FALSO"));
            stream.Flush();
            await Task.Delay(1200);
            Assert.Equal(0, monitor.Snapshot.Detections);
        }

        await WaitUntilAsync(() => monitor.Snapshot.Detections == 1);
        Assert.Equal(0, monitor.Snapshot.ReadErrors);
    }

    [Fact]
    public async Task Arquivo_sem_alteracao_nao_e_verificado_de_novo_e_marcado_e_ignorado()
    {
        using var dir = new TempDir();
        var engine = new FakeEngine();
        await using var monitor = await StartAsync(Create(engine, dir.Path));

        var path = dir.WriteFile("a.txt", "conteúdo");
        await WaitUntilAsync(() => monitor.Snapshot.Scanned == 1);
        var calls = engine.ScanCalls;
        monitor.Enqueue(path);
        monitor.Enqueue(path);
        await Task.Delay(1000);
        Assert.Equal(calls, engine.ScanCalls);

        var restored = dir.WriteFile("restaurado.txt", "MALWARE-FALSO");
        monitor.MarkHandled(restored);
        await Task.Delay(1000);
        Assert.Equal(0, monitor.Snapshot.Detections);
    }

    [Fact]
    public async Task Pasta_movida_para_a_pasta_vigiada_tem_os_arquivos_verificados()
    {
        using var outside = new TempDir();
        using var dir = new TempDir();
        outside.WriteFile(Path.Combine("pacote", "dentro", "ameaca.txt"), "MALWARE-FALSO");
        var engine = new FakeEngine();
        await using var monitor = await StartAsync(Create(engine, dir.Path));

        Directory.Move(outside.Combine("pacote"), dir.Combine("pacote"));

        await WaitUntilAsync(() => monitor.Snapshot.Detections == 1);
    }

    [Fact]
    public async Task Motor_indisponivel_fica_registrado_e_pasta_inexistente_e_informada()
    {
        using var dir = new TempDir();
        var monitor = new RealTimeMonitor(_ => Task.FromResult<IScanEngine>(new FakeEngine { Unavailable = true }),
            new RealTimeOptions([dir.Path, dir.Combine("não existe")], [], new ScanOptions(1024, 1, TimeSpan.FromSeconds(5)))
            {
                EngineRetryDelay = TimeSpan.FromMilliseconds(100),
            });
        await using (monitor)
        {
            monitor.Start();
            await WaitUntilAsync(() => monitor.Snapshot.State == RealTimeState.EngineUnavailable);
            Assert.Contains("motor de teste desligado", monitor.Snapshot.Message, StringComparison.Ordinal);
            Assert.Single(monitor.Snapshot.MissingFolders);
            Assert.Single(monitor.Snapshot.WatchedFolders);
        }

        Assert.Equal(RealTimeState.Stopped, monitor.Snapshot.State);
    }

    [Theory]
    [InlineData(new[] { "--verificar", @"C:\a b\x.txt", @"D:\y" }, true, false, null, 2)]
    [InlineData(new[] { "--bandeja", "--dados", @"D:\dados" }, false, true, @"D:\dados", 0)]
    [InlineData(new[] { @"C:\pasta" }, false, false, null, 1)]
    [InlineData(new[] { "--desconhecida" }, false, false, null, 0)]
    public void Interpreta_a_linha_de_comando(string[] args, bool scan, bool tray, string? data, int paths)
    {
        var options = CommandLineOptions.Parse(args);

        Assert.Equal(scan, options.ScanNow);
        Assert.Equal(tray, options.StartInTray);
        Assert.Equal(data, options.DataDir);
        Assert.Equal(paths, options.Paths.Count);
    }

    [Fact]
    public void Raiz_de_unidade_entre_aspas_e_corrigida()
    {
        var options = CommandLineOptions.Parse(["--verificar", "C:\""]);

        Assert.Equal(@"C:\", Assert.Single(options.Paths));
    }

    [Fact]
    public void Caminho_relativo_vira_absoluto_na_pasta_atual()
    {
        var options = CommandLineOptions.Parse(["--verificar", @"sub pasta\arquivo ç.txt", "--dados", "dados"]);

        Assert.Equal(Path.Combine(Environment.CurrentDirectory, @"sub pasta\arquivo ç.txt"), Assert.Single(options.Paths));
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "dados"), options.DataDir);
    }
}
