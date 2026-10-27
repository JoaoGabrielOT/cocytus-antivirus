using Cocytus.Maintenance;
using Microsoft.Win32;

namespace Cocytus.Tests;

/// <summary>Chave descartável em HKCU\Software\Cocytus.Tests, apagada no fim.</summary>
internal sealed class TempRegistryKey : IDisposable
{
    public TempRegistryKey()
    {
        Path = @"Software\Cocytus.Tests\" + Guid.NewGuid().ToString("N")[..12];
        Registry.CurrentUser.CreateSubKey(Path).Dispose();
    }

    public string Path { get; }

    public RegistryKey Create(string relative) => Registry.CurrentUser.CreateSubKey(Path + "\\" + relative, writable: true);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Cocytus.Tests", writable: true);
        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\Cocytus.Tests", throwOnMissingSubKey: false);
        }
    }
}

public sealed class MaintenanceTests
{
    [Fact]
    public void Inicializacao_lista_e_desativa_como_o_gerenciador_de_tarefas()
    {
        using var reg = new TempRegistryKey();
        using var dir = new TempDir();
        using (var run = reg.Create("Run"))
        {
            run.SetValue("Ativo", @"""C:\Programa Teste\ativo.exe"" --bandeja");
            run.SetValue("Desligado", @"C:\Teste\desligado.exe");
        }

        using (var approved = reg.Create(@"Approved\Run"))
        {
            approved.SetValue("Desligado", StartupManager.ApprovedData(enabled: false, DateTime.UtcNow), RegistryValueKind.Binary);
        }

        dir.WriteFile("Atalho.lnk", "atalho de teste");
        dir.WriteFile("desktop.ini", "[.ShellClassInfo]");
        var locations = new StartupLocations(reg.Path + @"\Run", "", "", reg.Path + @"\Approved", "", dir.Path, "", IncludeMachine: false);
        var manager = new StartupManager(locations);

        var items = manager.List();

        Assert.Equal(3, items.Count);
        var active = Assert.Single(items, i => i.Name == "Ativo");
        Assert.True(active.Enabled);
        Assert.Equal(@"C:\Programa Teste\ativo.exe", active.FilePath);
        Assert.False(Assert.Single(items, i => i.Name == "Desligado").Enabled);
        var shortcut = Assert.Single(items, i => i.Source == StartupSource.Folder);
        Assert.Equal("Atalho", shortcut.Name);
        Assert.True(shortcut.Enabled);

        Assert.True(manager.SetEnabled(active, enabled: false));
        Assert.True(manager.SetEnabled(shortcut, enabled: false));
        Assert.False(manager.List().Single(i => i.Name == "Ativo").Enabled);
        Assert.False(manager.List().Single(i => i.Name == "Atalho").Enabled);
        // Desativar não mexe no item: o comando continua no Run e o atalho na pasta.
        using (var run = reg.Create("Run"))
        {
            Assert.NotNull(run.GetValue("Ativo"));
        }

        Assert.True(File.Exists(dir.Combine("Atalho.lnk")));

        Assert.True(manager.SetEnabled(manager.List().Single(i => i.Name == "Ativo"), enabled: true));
        Assert.True(manager.List().Single(i => i.Name == "Ativo").Enabled);
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    public void Estado_de_StartupApproved(byte[] data, bool enabled)
    {
        Assert.Equal(enabled, StartupManager.IsEnabled(data));
        Assert.True(StartupManager.IsEnabled(null));
    }

    [Fact]
    public void Limpeza_apaga_so_arquivos_antigos_e_nao_segue_juncoes()
    {
        using var dir = new TempDir();
        using var outside = new TempDir();
        var old = DateTime.UtcNow.AddDays(-3);
        var oldFile = dir.WriteFile("velho.tmp", new string('x', 100));
        var oldNested = dir.WriteFile("pasta velha/dentro.tmp", "x");
        var recent = dir.WriteFile("recente.tmp", "x");
        var readOnly = dir.WriteFile("somente-leitura.tmp", "x");
        var protectedFile = outside.WriteFile("nao-mexer.txt", "x");
        foreach (var file in new[] { oldFile, oldNested, readOnly, protectedFile })
        {
            File.SetCreationTimeUtc(file, old);
            File.SetLastWriteTimeUtc(file, old);
        }

        Directory.SetCreationTimeUtc(dir.Combine("pasta velha"), old);
        Directory.SetLastWriteTimeUtc(dir.Combine("pasta velha"), old);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var junction = TestEnvironment.TryCreateJunction(dir.Combine("link"), outside.Path);
        var category = new CleanupCategory("teste", "Teste", "", [new CleanupRoot(dir.Path, "*", Recursive: true, TimeSpan.FromHours(24))]);

        var estimate = TempCleaner.Analyze(category, CancellationToken.None);
        var result = TempCleaner.Clean(category, CancellationToken.None);

        Assert.Equal(2, estimate.Files);
        Assert.Equal(2, result.Deleted);
        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(oldNested));
        Assert.False(Directory.Exists(dir.Combine("pasta velha")));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(readOnly));
        Assert.True(File.Exists(protectedFile));
        if (junction)
        {
            Assert.True(Directory.Exists(dir.Combine("link")));
        }
    }

    [Fact]
    public void Limpeza_respeita_o_filtro_e_recusa_pastas_raiz()
    {
        using var dir = new TempDir();
        var thumb = dir.WriteFile("thumbcache_256.db", "x");
        var other = dir.WriteFile("iconcache_256.db", "x");
        var category = new CleanupCategory("thumbs", "Miniaturas", "", [new CleanupRoot(dir.Path, "thumbcache_*.db", Recursive: false, TimeSpan.Zero)]);

        TempCleaner.Clean(category, CancellationToken.None);

        Assert.False(File.Exists(thumb));
        Assert.True(File.Exists(other));
        Assert.False(TempCleaner.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(TempCleaner.IsSafeRoot(Path.GetPathRoot(Environment.SystemDirectory)!));
        Assert.False(TempCleaner.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(TempCleaner.IsSafeRoot(dir.Path));
    }

    [Fact]
    public void Lista_programas_com_as_regras_de_programas_e_recursos()
    {
        using var reg = new TempRegistryKey();
        using (var key = reg.Create(@"Uninstall\Comum"))
        {
            key.SetValue("DisplayName", "Programa Comum");
            key.SetValue("Publisher", "Editora");
            key.SetValue("DisplayVersion", "1.2.3");
            key.SetValue("InstallDate", "20260115");
            key.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
            key.SetValue("UninstallString", @"""C:\Program Files\Comum\unins000.exe"" /SILENT");
        }

        using (var key = reg.Create(@"Uninstall\{11111111-2222-3333-4444-555555555555}"))
        {
            key.SetValue("DisplayName", "Programa MSI");
            key.SetValue("WindowsInstaller", 1, RegistryValueKind.DWord);
            key.SetValue("UninstallString", "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}");
        }

        using (var key = reg.Create(@"Uninstall\Oculto"))
        {
            key.SetValue("DisplayName", "Componente");
            key.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
            key.SetValue("UninstallString", "x.exe");
        }

        using (var key = reg.Create(@"Uninstall\Atualizacao"))
        {
            key.SetValue("DisplayName", "Atualização do Programa");
            key.SetValue("ParentKeyName", "Comum");
            key.SetValue("UninstallString", "x.exe");
        }

        using (var key = reg.Create(@"Uninstall\SemNome"))
        {
            key.SetValue("UninstallString", "x.exe");
        }

        var apps = InstalledApps.ListDesktop(new UninstallLocations([(RegistryHive.CurrentUser, reg.Path + @"\Uninstall")]));

        Assert.Equal(new[] { "Programa Comum", "Programa MSI" }, apps.Select(a => a.Name));
        var common = apps[0];
        Assert.Equal(new DateTime(2026, 1, 15), common.InstallDate);
        Assert.Equal(2048 * 1024L, common.SizeBytes);
        Assert.False(common.AllUsers);
        Assert.Equal((@"C:\Program Files\Comum\unins000.exe", "/SILENT"), InstalledApps.UninstallCommand(common) ?? default);
        var msi = apps[1];
        Assert.True(msi.IsMsi);
        var (file, arguments) = InstalledApps.UninstallCommand(msi) ?? default;
        Assert.EndsWith("msiexec.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/x {11111111-2222-3333-4444-555555555555}", arguments);

        Assert.True(InstalledApps.StillInstalled(common));
        Registry.CurrentUser.DeleteSubKeyTree(reg.Path + @"\Uninstall\Comum");
        Assert.False(InstalledApps.StillInstalled(common));
    }

    [Fact]
    public void Restos_so_com_nome_exato_e_sem_pastas_protegidas_ou_de_outros_programas()
    {
        using var dir = new TempDir();
        using var reg = new TempRegistryKey();
        var programFiles = dir.Combine("Program Files");
        var appData = dir.Combine("AppData");
        Directory.CreateDirectory(dir.Combine("Program Files", "Programa Teste"));
        dir.WriteFile(@"Program Files\Programa Teste\resto.dll", "x");
        dir.WriteFile(@"AppData\Editora Teste\Programa Teste\config.ini", "x");
        dir.WriteFile(@"AppData\Editora Teste\Outro Programa\config.ini", "x");
        dir.WriteFile(@"AppData\Programa Testes\nao-e-igual.txt", "x");
        dir.WriteFile(@"AppData\Programa Teste 2.1 (x64)\cache.bin", "x");
        Directory.CreateDirectory(dir.Combine("Program Files", "Compartilhado"));
        reg.Create(@"Software\Editora Teste\Programa Teste").Dispose();
        reg.Create(@"Software\Programa Teste").Dispose();
        reg.Create(@"Software\Microsoft\Programa Teste").Dispose();

        var roots = new LeftoverRoots(
            [(programFiles, true), (appData, false)],
            [(RegistryHive.CurrentUser, reg.Path + @"\Software")],
            []);
        var app = new InstalledApp(AppKind.Desktop, "Programa Teste 2.1 (x64)", "Editora Teste Ltd.", "2.1", null, null,
            dir.Combine("Program Files", "Compartilhado"), "x.exe", false, null, false, "HKCU", "x", null);
        var other = app with { Name = "Outro", InstallLocation = dir.Combine("AppData", "Editora Teste", "Outro Programa") };

        var found = LeftoverFinder.Find(app, [other], roots);
        var paths = found.Select(f => f.Path).ToList();

        Assert.Contains(dir.Combine("Program Files", "Programa Teste"), paths);
        Assert.Contains(dir.Combine("AppData", "Editora Teste", "Programa Teste"), paths);
        Assert.Contains(dir.Combine("AppData", "Programa Teste 2.1 (x64)"), paths);
        Assert.Contains(dir.Combine("Program Files", "Compartilhado"), paths);
        Assert.DoesNotContain(dir.Combine("AppData", "Editora Teste"), paths);
        Assert.DoesNotContain(dir.Combine("AppData", "Programa Testes"), paths);
        Assert.DoesNotContain(dir.Combine("AppData", "Editora Teste", "Outro Programa"), paths);
        Assert.Contains(@"HKCU\" + reg.Path + @"\Software\Programa Teste", paths);
        Assert.Contains(@"HKCU\" + reg.Path + @"\Software\Editora Teste\Programa Teste", paths);
        Assert.DoesNotContain(paths, p => p.Contains(@"\Microsoft\", StringComparison.Ordinal));
        Assert.True(found.Single(f => f.Path == dir.Combine("Program Files", "Programa Teste")).NeedsAdmin);

        // A pasta de instalação que contém outro programa instalado nunca é sugerida.
        var shared = app with { InstallLocation = dir.Combine("AppData", "Editora Teste") };
        Assert.DoesNotContain(dir.Combine("AppData", "Editora Teste"), LeftoverFinder.Find(shared, [other], roots).Select(f => f.Path));
    }

    [Fact]
    public void Nomes_de_produto_e_editor()
    {
        Assert.Equal(new[] { "Programa Teste 2.1 (x64)", "Programa Teste 2.1", "Programa Teste" }, LeftoverFinder.ProductNames("Programa Teste 2.1 (x64)"));
        Assert.Equal(new[] { "Editora Exemplo, Inc", "Editora Exemplo" }, LeftoverFinder.PublisherNames("Editora Exemplo, Inc."));
        Assert.Empty(LeftoverFinder.ProductNames("Microsoft"));
    }

    [Fact]
    public async Task Painel_de_uso_le_discos_memoria_e_processos()
    {
        var snapshot = await SystemUsage.CaptureAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.NotEmpty(snapshot.Drives);
        Assert.True(snapshot.Memory.TotalBytes > 0);
        Assert.InRange(snapshot.Memory.LoadPercent, 0u, 100u);
        Assert.Contains(snapshot.Processes, p => p.WorkingSetBytes > 0);
        Assert.All(snapshot.Processes, p => Assert.InRange(p.CpuPercent, 0, 100));
    }

    [Fact]
    public void Lixeira_e_apps_da_store_podem_ser_consultados()
    {
        var (items, bytes) = RecycleBin.Query();
        Assert.True(items >= 0 && bytes >= 0);

        var store = StoreApps.List();
        Assert.All(store, a => Assert.False(string.IsNullOrWhiteSpace(a.PackageFullName)));
    }
}
