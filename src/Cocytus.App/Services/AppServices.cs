using System.Globalization;
using Cocytus.Core;
using Cocytus.Core.Protection;
using Cocytus.Core.Scanning;
using Cocytus.Data;
using Cocytus.Data.History;
using Cocytus.Data.Quarantine;
using Cocytus.Engines.ClamAv;
using Cocytus.Update;

namespace Cocytus.App.Services;

/// <summary>Composição dos serviços do aplicativo. A interface só usa o que está aqui.</summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly SemaphoreSlim _realTimeLock = new(1, 1);
    private ClamAvEngine? _engine;
    private int _busy;
    private string? _lastRealTimeMessage;

    public AppServices(CocytusPaths paths)
    {
        Paths = paths;
        Paths.EnsureCreated();
        Log = new AppLog(paths.LogsDir);
        SettingsStore = new SettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load(out var warning);
        SettingsWarning = warning;
        History = new HistoryStore(paths.HistoryDatabase);
        Quarantine = new QuarantineStore(paths.QuarantineDir);
        Log.Info($"Cocytus iniciado. Dados: {paths.DataRoot}. Motor: {paths.ClamAvDir}");
        if (warning is not null)
        {
            Log.Error(warning);
        }
    }

    public CocytusPaths Paths { get; }

    public AppLog Log { get; }

    public SettingsStore SettingsStore { get; }

    public CocytusSettings Settings { get; private set; }

    public string? SettingsWarning { get; }

    public HistoryStore History { get; }

    public QuarantineStore Quarantine { get; }

    /// <summary>Disparado quando o motor, as bases, o tempo real ou o estado de ocupado mudam (pode vir de outra thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Ameaça ou suspeito encontrado pelo tempo real (vem de outra thread).</summary>
    public event EventHandler<FileScanResult>? RealTimeDetected;

    public RealTimeMonitor? RealTime { get; private set; }

    /// <summary>Pastas que nenhuma varredura percorre: dados do Cocytus (quarentena, bases) e a pasta do programa.</summary>
    public IReadOnlyList<string> ScanExclusions => [Paths.DataRoot, AppContext.BaseDirectory];

    /// <summary>Cópias de segurança (.reg) das chaves de registro removidas na limpeza de restos.</summary>
    public string RegistryBackupDir => Path.Combine(Paths.DataRoot, "backups", "registro");

    /// <summary>Pastas vigiadas: as configuradas ou, se nenhuma, as padrão do usuário.</summary>
    public IReadOnlyList<string> RealTimeFolders => Settings.RealTimeFolders ?? DefaultRealTimeFolders();

    public static IReadOnlyList<string> DefaultRealTimeFolders()
    {
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(full);
                }
            }
        }

        Add(NativeMethods.GetDownloadsFolder());
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Path.GetTempPath());
        return folders;
    }

    /// <summary>Liga, desliga ou reinicia o tempo real conforme as configurações atuais.</summary>
    public async Task ApplyRealTimeAsync()
    {
        await _realTimeLock.WaitAsync();
        try
        {
            if (RealTime is { } current)
            {
                current.Detected -= OnRealTimeDetected;
                current.StateChanged -= OnRealTimeStateChanged;
                await current.StopAsync();
                RealTime = null;
                Log.Info("Tempo real parado.");
            }

            if (Settings.RealTimeEnabled)
            {
                var options = new RealTimeOptions(RealTimeFolders, ScanExclusions, ScanOptions.FromSettings(Settings));
                var monitor = new RealTimeMonitor(async _ => (IScanEngine)await GetEngineAsync(), options);
                monitor.Detected += OnRealTimeDetected;
                monitor.StateChanged += OnRealTimeStateChanged;
                monitor.Start();
                RealTime = monitor;
                Log.Info($"Tempo real iniciado: {string.Join(" | ", monitor.Snapshot.WatchedFolders)}");
            }
        }
        finally
        {
            _realTimeLock.Release();
        }

        RaiseStateChanged();
    }

    private void OnRealTimeStateChanged(object? sender, EventArgs e)
    {
        if (sender is RealTimeMonitor monitor && monitor.Snapshot is { Message: { } message } snapshot && message != _lastRealTimeMessage)
        {
            _lastRealTimeMessage = message;
            Log.Info($"Tempo real ({snapshot.State}): {message}");
        }

        RaiseStateChanged();
    }

    private void OnRealTimeDetected(object? sender, FileScanResult result)
    {
        Log.Info($"Tempo real: {Texts.Outcome(result.Outcome)} {result.ThreatName} em {result.Path}");
        try
        {
            History.AddEvent(HistoryStore.EventRealTime, $"{Texts.Outcome(result.Outcome)} pelo tempo real ({result.ThreatName}).", result.Path);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Log.Error("Falha ao registrar detecção no histórico", ex);
        }

        RealTimeDetected?.Invoke(this, result);
    }

    /// <summary>
    /// Move os itens para a quarentena (o chamador já pediu confirmação ao usuário).
    /// Devolve, para cada item, o erro (ou nulo) e se o original ainda aparece no disco.
    /// </summary>
    public async Task<IReadOnlyList<(FileScanResult Item, string? Error, bool StillPresent)>> QuarantineAsync(
        IReadOnlyList<FileScanResult> items, string engineName, string? databaseVersion)
    {
        var outcome = new List<(FileScanResult, string?, bool)>();
        foreach (var item in items)
        {
            try
            {
                var added = await Task.Run(() => Quarantine.Add(item.Path, item.ThreatName ?? Texts.Outcome(item.Outcome), engineName, databaseVersion, item.Sha256));
                History.AddEvent(HistoryStore.EventQuarantine, $"Movido para a quarentena ({item.ThreatName}).", item.Path);
                Log.Info($"Quarentena: {item.Path} ({item.ThreatName}) -> {added.Item.Id}");
                outcome.Add((item, null, added.OriginalStillPresent));
            }
            catch (QuarantineException ex)
            {
                History.AddEvent(HistoryStore.EventError, "Falha ao mover para a quarentena: " + ex.Message, item.Path);
                Log.Error($"Falha na quarentena de {item.Path}", ex);
                outcome.Add((item, ex.Message, false));
            }
        }

        return outcome;
    }

    /// <summary>Versão da base daily instalada (para registrar junto da quarentena).</summary>
    public string? DailyDatabaseVersion =>
        ClamAvDatabase.ReadAll(Paths.ClamAvDatabaseDir).FirstOrDefault(d => d.Name == "daily")?.Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Uma varredura ou atualização em andamento (evita as duas ao mesmo tempo).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public bool TryBeginBusy()
    {
        var ok = Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        if (ok)
        {
            RaiseStateChanged();
        }

        return ok;
    }

    public void EndBusy()
    {
        Volatile.Write(ref _busy, 0);
        RaiseStateChanged();
    }

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public ClamAvOptions CurrentClamAvOptions() => ClamAvOptions.FromPaths(Paths, Settings);

    public async Task<ClamAvEngine> GetEngineAsync()
    {
        await _engineLock.WaitAsync();
        try
        {
            _engine ??= new ClamAvEngine(CurrentClamAvOptions());
            return _engine;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>Encerra o clamd só se o tempo real não estiver usando o motor.</summary>
    public async Task ReleaseEngineIfIdleAsync()
    {
        if (RealTime is null)
        {
            await ReleaseEngineAsync();
        }
    }

    /// <summary>Encerra o clamd (libera memória). Ele sobe de novo na próxima varredura.</summary>
    public async Task ReleaseEngineAsync()
    {
        await _engineLock.WaitAsync();
        try
        {
            if (_engine is not null)
            {
                await _engine.DisposeAsync();
                _engine = null;
                Log.Info("Motor ClamAV encerrado.");
            }
        }
        finally
        {
            _engineLock.Release();
        }

        RaiseStateChanged();
    }

    public bool EngineRunning => _engine?.IsRunning == true;

    /// <param name="settings">Configurações novas.</param>
    /// <param name="recordEvent">Registra "Configurações alteradas" no histórico (falso para dados internos, como a data da última consulta de versão).</param>
    public async Task SaveSettingsAsync(CocytusSettings settings, bool recordEvent = true)
    {
        var normalized = settings.Clone().Normalize();
        SettingsStore.Save(normalized);
        var restartEngine = !normalized.EngineSettingsEqual(Settings);
        var restartRealTime = !normalized.RealTimeSettingsEqual(Settings);
        Settings = normalized;
        if (recordEvent)
        {
            Log.Info("Configurações salvas.");
            History.AddEvent(HistoryStore.EventSettings, "Configurações alteradas.");
        }
        if (restartEngine)
        {
            // Os limites ficam no clamd.conf: o motor é recriado na próxima varredura.
            await ReleaseEngineAsync();
        }

        if (restartRealTime)
        {
            await ApplyRealTimeAsync();
        }
    }

    public FreshclamUpdater CreateUpdater() => new(CurrentClamAvOptions());

    public bool EngineInstalled => File.Exists(Path.Combine(Paths.ClamAvDir, "clamd.exe"));

    public string EngineVersionText
    {
        get
        {
            var versionFile = Path.Combine(Paths.ClamAvDir, "VERSION.txt");
            try
            {
                return File.Exists(versionFile) ? File.ReadLines(versionFile).FirstOrDefault()?.Trim() ?? "ClamAV" : "ClamAV (versão desconhecida)";
            }
            catch (IOException)
            {
                return "ClamAV";
            }
        }
    }

    /// <summary>Resumo curto do estado do motor para o rodapé da navegação.</summary>
    public string EngineStatusText
    {
        get
        {
            if (!EngineInstalled)
            {
                return "Motor ClamAV não encontrado.";
            }

            var daily = ClamAvDatabase.ReadAll(Paths.ClamAvDatabaseDir).FirstOrDefault(d => d.Name == "daily");
            if (daily is null)
            {
                return "Bases não instaladas. Abra Atualização.";
            }

            var date = daily.BuildTime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")) ?? "data desconhecida";
            return $"Bases do ClamAV: daily {daily.Version} ({date})" + (EngineRunning ? " · motor carregado" : "");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (RealTime is { } monitor)
        {
            await monitor.StopAsync();
            RealTime = null;
        }

        await ReleaseEngineAsync();
        _engineLock.Dispose();
        _realTimeLock.Dispose();
    }
}
