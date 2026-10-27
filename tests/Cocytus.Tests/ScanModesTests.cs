using Cocytus.Core;
using Cocytus.Core.Scanning;
using Cocytus.Maintenance;

namespace Cocytus.Tests;

public sealed class ScanModesTests
{
    private static ScanOptions Options() => new(1024 * 1024, 2, TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Pasta_restrita_analisa_so_executaveis_e_scripts()
    {
        using var dir = new TempDir();
        dir.WriteFile("app/programa.exe", "MALWARE-FALSO");
        dir.WriteFile("app/script.ps1", "conteúdo comum");
        dir.WriteFile("app/dados.json", "MALWARE-FALSO, mas não é executável");
        dir.WriteFile("app/cache/imagem.png", "conteúdo comum");

        var request = new ScanRequest(ScanKind.Quick, [new ScanTarget(dir.Path, ExecutablesOnly: true)], []);
        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync(request, null, CancellationToken.None);

        Assert.Equal(ScanKind.Quick, report.Kind);
        Assert.Equal(2, report.Processed);
        Assert.Equal(1, report.Threats);
        Assert.EndsWith("programa.exe", Assert.Single(report.Items).Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pasta_completa_dentro_de_restrita_e_verificada_uma_vez_e_inteira()
    {
        using var dir = new TempDir();
        dir.WriteFile("Local/app.dll", "conteúdo comum");
        dir.WriteFile("Local/nota.txt", "não entra");
        dir.WriteFile("Local/Temp/baixado.txt", "MALWARE-FALSO");
        dir.WriteFile("Local/Temp/outro.exe", "conteúdo comum");
        var engine = new FakeEngine();

        var request = new ScanRequest(ScanKind.Quick,
            [new ScanTarget(dir.Combine("Local"), ExecutablesOnly: true), new ScanTarget(dir.Combine("Local", "Temp"))], []);
        var report = await new ScanOrchestrator(engine, Options()).RunAsync(request, null, CancellationToken.None);

        Assert.Equal(3, report.Processed);
        Assert.Equal(3, engine.ScanCalls);
        Assert.Equal(1, report.Threats);
    }

    [Fact]
    public async Task Pasta_excluida_nao_e_percorrida_e_descricao_vira_alvo_do_relatorio()
    {
        using var dir = new TempDir();
        dir.WriteFile("disco/arquivo.txt", "conteúdo comum");
        dir.WriteFile("disco/Cocytus/quarentena/item.bin", "MALWARE-FALSO");
        var processo = dir.WriteFile("disco/programa.exe", "conteúdo comum");

        var request = new ScanRequest(ScanKind.Full,
            [new ScanTarget(dir.Combine("disco")), new ScanTarget(processo)],
            [dir.Combine("disco", "Cocytus")],
            ["Verificação completa: teste"]);
        var report = await new ScanOrchestrator(new FakeEngine(), Options()).RunAsync(request, null, CancellationToken.None);

        Assert.Equal(2, report.Processed);
        Assert.Equal(0, report.Threats);
        Assert.Equal(new[] { "Verificação completa: teste" }, report.Targets);
        Assert.Equal(ScanKind.Full, report.Kind);
    }

    [Fact]
    public void Normalizacao_respeita_alvos_restritos()
    {
        using var dir = new TempDir();
        var exe = dir.WriteFile("a/b/programa.exe", "x");
        var txt = dir.WriteFile("a/texto.txt", "x");
        var sub = dir.Combine("a", "b");

        var targets = FileEnumerator.NormalizeTargets(
            [new ScanTarget(dir.Combine("a"), ExecutablesOnly: true), new ScanTarget(exe), new ScanTarget(txt), new ScanTarget(sub), new ScanTarget(sub, ExecutablesOnly: true)]);

        // O .exe já é coberto pela pasta restrita; o .txt não; a subpasta pedida inteira continua (e prevalece sobre a versão restrita).
        Assert.Equal(3, targets.Count);
        Assert.DoesNotContain(targets, t => t.Path == exe);
        Assert.Contains(targets, t => t.Path == txt);
        Assert.Contains(targets, t => t.Path == sub && !t.ExecutablesOnly);
    }

    [Fact]
    public void Verificacao_rapida_monta_alvos_a_partir_das_fontes()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Programas\app.exe", @"C:\Programas\inicia.exe", @"C:\Users\x\Downloads", @"C:\Users\x\AppData\Roaming",
        };
        var sources = new QuickScanSources(
            [@"C:\Programas\app.exe", @"C:\Programas\app.exe", @"C:\Sumiu\velho.exe"],
            [@"C:\Programas\inicia.exe"],
            [@"C:\Users\x\Startup-inexistente"],
            [@"C:\Users\x\Downloads"],
            [@"C:\Users\x\AppData\Roaming", @"C:\Users\x\AppData\LocalLow"]);

        var request = ScanLocations.Quick(sources, [@"C:\Dados"], existing.Contains, existing.Contains);

        Assert.Equal(ScanKind.Quick, request.Kind);
        Assert.Equal(
            new[] { @"C:\Programas\app.exe", @"C:\Programas\inicia.exe", @"C:\Users\x\Downloads", @"C:\Users\x\AppData\Roaming" },
            request.Targets.Select(t => t.Path));
        Assert.True(request.Targets.Single(t => t.Path.EndsWith("Roaming", StringComparison.Ordinal)).ExecutablesOnly);
        Assert.False(request.Targets.Single(t => t.Path.EndsWith("Downloads", StringComparison.Ordinal)).ExecutablesOnly);
        Assert.Equal(new[] { @"C:\Dados" }, request.ExcludedFolders);
        Assert.StartsWith("Verificação rápida: 2 programa(s) em execução e 1 arquivo(s) de inicialização", request.Description![0], StringComparison.Ordinal);
    }

    [Fact]
    public void Verificacao_completa_usa_as_unidades_fixas()
    {
        var drives = ScanLocations.FixedDrives();
        Assert.NotEmpty(drives);
        Assert.Contains(drives, d => d.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase));

        var request = ScanLocations.Full(drives, []);
        Assert.Equal(ScanKind.Full, request.Kind);
        Assert.All(request.Targets, t => Assert.False(t.ExecutablesOnly));
    }

    [Fact]
    public void Coleta_da_verificacao_rapida_encontra_o_proprio_processo()
    {
        var sources = ScanLocations.CollectQuickSources();
        var self = Environment.ProcessPath!;

        Assert.Contains(sources.ProcessImages, p => p.Equals(self, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(sources.UserFolders);
        Assert.NotEmpty(sources.AppDataFolders);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --tray", @"C:\Program Files\App\app.exe", "--tray")]
    [InlineData(@"C:\Program Files\App\app.exe --tray", @"C:\Program Files\App\app.exe", "--tray")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe", "")]
    [InlineData(@"MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "MsiExec.exe", "/X{11111111-2222-3333-4444-555555555555}")]
    public void Separa_executavel_e_argumentos(string command, string file, string arguments)
    {
        var exists = (string path) => path.Equals(@"C:\Program Files\App\app.exe", StringComparison.OrdinalIgnoreCase);

        var split = CommandLines.Split(command, exists);

        var (fileName, args) = split ?? ("(nulo)", "(nulo)");
        Assert.Equal(file, fileName);
        Assert.Equal(arguments, args);
    }

    [Fact]
    public void Arquivos_de_inicializacao_incluem_a_dll_do_rundll32_e_caminhos_de_servico()
    {
        var system = Environment.SystemDirectory;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var exists = (string path) => path.Equals(Path.Combine(system, "rundll32.exe"), StringComparison.OrdinalIgnoreCase);

        var files = CommandLines.ExtractFiles(@"rundll32.exe ""C:\Pasta X\lib.dll"",Entrada", exists);

        Assert.Equal(new[] { Path.Combine(system, "rundll32.exe"), @"C:\Pasta X\lib.dll" }, files);
        Assert.Equal(Path.Combine(windows, @"system32\drivers\x.sys"), CommandLines.NormalizeServicePath(@"\SystemRoot\system32\drivers\x.sys"));
        Assert.Equal(@"C:\x\svc.exe", CommandLines.NormalizeServicePath(@"\??\C:\x\svc.exe"));
    }

    [Fact]
    public void Tipos_executaveis()
    {
        Assert.True(ExecutableTypes.Matches(@"C:\a\B.EXE"));
        Assert.True(ExecutableTypes.Matches("script.ps1"));
        Assert.False(ExecutableTypes.Matches("foto.jpg"));
        Assert.False(ExecutableTypes.Matches("sem-extensao"));
    }
}
