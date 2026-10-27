using Cocytus.Core;
using Cocytus.Data;
using Cocytus.Data.History;

namespace Cocytus.Tests;

public sealed class StorageTests
{
    [Fact]
    public void Historico_grava_e_le_varreduras_e_eventos()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(dir.Combine("historico.db"));
        var engine = new EngineInfo("ClamAV", "1.5.4", "28163", new DateTimeOffset(2026, 10, 24, 7, 23, 0, TimeSpan.Zero), "limites");
        var report = new ScanReport(Guid.NewGuid(), [@"C:\pasta com espaço"], DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now,
            ScanStatus.Completed, null, engine, 10, 1, 0, 2, 1, 12345,
            [
                new FileScanResult(@"C:\pasta com espaço\ação.exe", ScanOutcome.Threat, NotScannedReason.None, "Win.Test", "Assinatura", 10, "abc"),
                new FileScanResult(@"C:\x.zip", ScanOutcome.NotScanned, NotScannedReason.ArchiveLimitsExceeded, null, "limite", null, null),
            ],
            false);

        store.SaveScan(report);
        store.AddEvent(HistoryStore.EventQuarantine, "Movido", @"C:\pasta com espaço\ação.exe");

        var scan = Assert.Single(store.ListScans());
        Assert.Equal(report.Id.ToString("N"), scan.Id);
        Assert.Equal(ScanStatus.Completed, scan.Status);
        Assert.Equal(10, scan.NoThreat);
        Assert.Equal(1, scan.Threats);
        Assert.Equal("28163", scan.DatabaseVersion);
        Assert.Equal(engine.DatabaseDate, scan.DatabaseDate);
        Assert.Equal(@"C:\pasta com espaço", Assert.Single(scan.Targets));

        var items = store.GetScanItems(scan.Id);
        Assert.Equal(2, items.Count);
        Assert.Equal(report.Items[0], items[0]);
        Assert.Equal(NotScannedReason.ArchiveLimitsExceeded, items[1].Reason);

        var ev = Assert.Single(store.ListEvents());
        Assert.Equal(HistoryStore.EventQuarantine, ev.Kind);

        store.Clear();
        Assert.Empty(store.ListScans());
        Assert.Empty(store.ListEvents());
    }

    [Fact]
    public void Configuracoes_padrao_ida_e_volta_e_arquivo_corrompido()
    {
        using var dir = new TempDir();
        var path = dir.Combine("config", "configuracoes.json");
        var store = new SettingsStore(path);

        var defaults = store.Load(out var warning);
        Assert.Null(warning);
        Assert.Equal(200, defaults.MaxFileSizeMb);
        Assert.False(defaults.DetectPua);

        store.Save(new CocytusSettings { MaxFileSizeMb = 300, Parallelism = 4, DetectPua = true });
        var loaded = store.Load(out warning);
        Assert.Null(warning);
        Assert.Equal(300, loaded.MaxFileSizeMb);
        Assert.Equal(4, loaded.Parallelism);
        Assert.True(loaded.DetectPua);

        File.WriteAllText(path, "{ isto não é json");
        var fallback = store.Load(out warning);
        Assert.NotNull(warning);
        Assert.Equal(200, fallback.MaxFileSizeMb);
    }

    [Fact]
    public void Configuracoes_fora_da_faixa_sao_ajustadas()
    {
        var s = new CocytusSettings { MaxFileSizeMb = 0, Parallelism = 99, PerFileTimeoutSeconds = 1, ArchiveMaxRecursion = 1000 }.Normalize();

        Assert.Equal(1, s.MaxFileSizeMb);
        Assert.Equal(8, s.Parallelism);
        Assert.Equal(10, s.PerFileTimeoutSeconds);
        Assert.Equal(64, s.ArchiveMaxRecursion);
    }
}
