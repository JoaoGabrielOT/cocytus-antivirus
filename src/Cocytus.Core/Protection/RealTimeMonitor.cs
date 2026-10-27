using System.Collections.Concurrent;
using Cocytus.Core.Scanning;

namespace Cocytus.Core.Protection;

public sealed record RealTimeOptions(IReadOnlyList<string> Folders, IReadOnlyList<string> ExcludedFolders, ScanOptions Scan)
{
    /// <summary>Tempo sem novos eventos para considerar que o arquivo terminou de ser gravado.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Por quanto tempo tentar de novo um arquivo que ainda está em uso por quem o grava.</summary>
    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Limite da fila. Acima disso os eventos são descartados (e contados).</summary>
    public int MaxPending { get; init; } = 5000;

    /// <summary>Intervalo entre tentativas quando o motor está indisponível.</summary>
    public TimeSpan EngineRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Máximo de arquivos enfileirados quando uma pasta inteira aparece de uma vez (pasta movida).</summary>
    public int MaxFilesPerNewFolder { get; init; } = 2000;
}

public enum RealTimeState
{
    Stopped,
    Starting,
    Active,
    EngineUnavailable,
}

public sealed record RealTimeSnapshot(
    RealTimeState State,
    string? Message,
    long Scanned,
    long Detections,
    long NotScanned,
    long ReadErrors,
    long Overflows,
    long QueueFull,
    int Pending,
    IReadOnlyList<string> WatchedFolders,
    IReadOnlyList<string> MissingFolders,
    string? LastPath);

/// <summary>
/// Verificação em tempo real em modo usuário. Vigia pastas com FileSystemWatcher e analisa cada arquivo criado,
/// alterado ou renomeado logo depois que ele para de ser gravado. Não bloqueia a abertura nem a execução:
/// bloquear antes exigiria um minifiltro de kernel assinado pela Microsoft. Detecções são só informadas
/// (evento <see cref="Detected"/>); a decisão de mover para a quarentena fica com o usuário.
/// </summary>
public sealed class RealTimeMonitor : IAsyncDisposable
{
    private const int MaxRemembered = 20_000;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly string[] PartialDownloadExtensions = [".crdownload", ".part", ".partial", ".download", ".opdownload", ".!ut"];

    private readonly Func<CancellationToken, Task<IScanEngine>> _engineProvider;
    private readonly RealTimeOptions _options;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime WriteUtc)> _handled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _handledLock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _watched = [];
    private readonly List<string> _missing = [];
    private readonly string[] _excluded;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile RealTimeState _state = RealTimeState.Stopped;
    private volatile string? _message;
    private volatile string? _lastPath;
    private volatile bool _running;
    private bool _engineReady;
    private long _scanned;
    private long _detections;
    private long _notScanned;
    private long _readErrors;
    private long _overflows;
    private long _queueFull;
    private DateTime _lastQueueFullNotice;

    public RealTimeMonitor(Func<CancellationToken, Task<IScanEngine>> engineProvider, RealTimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(engineProvider);
        ArgumentNullException.ThrowIfNull(options);
        _engineProvider = engineProvider;
        _options = options;
        _excluded = options.ExcludedFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .ToArray();
    }

    /// <summary>Ameaça ou item suspeito encontrado. Disparado fora da thread da interface.</summary>
    public event EventHandler<FileScanResult>? Detected;

    /// <summary>Mudança de estado (ativo, motor indisponível, problema nos observadores).</summary>
    public event EventHandler? StateChanged;

    public RealTimeSnapshot Snapshot => new(_state, _message,
        Interlocked.Read(ref _scanned), Interlocked.Read(ref _detections), Interlocked.Read(ref _notScanned),
        Interlocked.Read(ref _readErrors), Interlocked.Read(ref _overflows), Interlocked.Read(ref _queueFull), _pending.Count,
        _watched.ToArray(), _missing.ToArray(), _lastPath);

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        foreach (var folder in _options.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _missing.Add(folder);
                continue;
            }

            if (!Directory.Exists(full))
            {
                _missing.Add(full);
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(full)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => Enqueue(e.FullPath);
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                _watched.Add(full);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _missing.Add(full);
            }
        }

        SetState(RealTimeState.Starting, _missing.Count > 0 ? $"Pastas não monitoradas (inexistentes ou inacessíveis): {string.Join("; ", _missing)}" : null);
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Esperado ao parar.
            }
        }

        _cts?.Dispose();
        _cts = null;
        _pending.Clear();
        SetState(RealTimeState.Stopped, null);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Marca o arquivo como já tratado (ex.: restaurado da quarentena pelo próprio usuário).</summary>
    public void MarkHandled(string path)
    {
        _pending.TryRemove(path, out _);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                Remember(path, info.Length, info.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (FileEnumerator.IsIoError(ex))
        {
            // Sem efeito.
        }
    }

    /// <summary>Coloca um caminho na fila (chamado pelos observadores; público para testes).</summary>
    public void Enqueue(string path)
    {
        if (!_running || string.IsNullOrEmpty(path) || IsIgnored(path))
        {
            return;
        }

        if (_pending.Count >= _options.MaxPending && !_pending.ContainsKey(path))
        {
            Interlocked.Increment(ref _queueFull);
            var noticeAt = DateTime.UtcNow;
            if (noticeAt - _lastQueueFullNotice > TimeSpan.FromMinutes(1))
            {
                _lastQueueFullNotice = noticeAt;
                _message = $"{DateTime.Now:HH:mm:ss}: fila cheia ({_options.MaxPending} arquivos aguardando); novos avisos estão sendo ignorados até a fila esvaziar.";
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var now = DateTime.UtcNow;
        _pending.AddOrUpdate(path,
            _ => new Pending(now, now + _options.SettleDelay, 0),
            (_, old) => old with { NotBefore = now + _options.SettleDelay });
    }

    private bool IsIgnored(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".cocytus-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        if (PartialDownloadExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            // Download incompleto: o arquivo final chega por renomeação e é verificado aí.
            return true;
        }

        foreach (var folder in _excluded)
        {
            if (path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || (path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    && path[folder.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Increment(ref _overflows);
        _message = $"{DateTime.Now:HH:mm:ss}: muitas alterações de uma vez; o Windows descartou parte dos avisos e alguns arquivos podem não ter sido verificados. "
            + e.GetException().Message;
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (sender is FileSystemWatcher watcher && _running)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ObjectDisposedException)
            {
                // A pasta pode ter sido removida.
            }
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!_engineReady)
            {
                try
                {
                    var engine = await _engineProvider(token);
                    await engine.EnsureReadyAsync(token);
                    _engineReady = true;
                    SetState(RealTimeState.Active, _missing.Count > 0 ? _message : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetState(RealTimeState.EngineUnavailable, "Motor indisponível: " + ex.Message);
                    await Task.Delay(_options.EngineRetryDelay, token);
                    continue;
                }
            }

            var now = DateTime.UtcNow;
            foreach (var entry in _pending)
            {
                if (token.IsCancellationRequested || !_engineReady)
                {
                    break;
                }

                if (entry.Value.NotBefore > now || !_pending.TryRemove(entry))
                {
                    continue;
                }

                try
                {
                    await ProcessAsync(entry.Key, entry.Value, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Falha inesperada num arquivo não derruba a vigilância.
                    Interlocked.Increment(ref _readErrors);
                    _message = $"Falha ao verificar {entry.Key}: {ex.Message}";
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            await Task.Delay(Tick, token);
        }
    }

    private async Task ProcessAsync(string path, Pending pending, CancellationToken token)
    {
        var item = FileEnumerator.ClassifyPath(path, out var isDirectory);
        if (isDirectory)
        {
            // Pasta que apareceu de uma vez (movida ou extraída): os arquivos dentro dela podem não gerar eventos próprios.
            var count = 0;
            foreach (var inner in FileEnumerator.Enumerate([path], token))
            {
                if (inner.Kind == ScanItemKind.File && count++ < _options.MaxFilesPerNewFolder)
                {
                    Enqueue(inner.Path);
                }
            }

            return;
        }

        if (item is not { Kind: ScanItemKind.File })
        {
            // Inexistente (arquivo temporário apagado), link ou arquivo só na nuvem: nada a fazer.
            return;
        }

        long length;
        DateTime writeUtc;
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            writeUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (FileEnumerator.IsIoError(ex))
        {
            return;
        }

        if (AlreadyHandled(path, length, writeUtc))
        {
            return;
        }

        FileScanResult result;
        try
        {
            var engine = await _engineProvider(token);
            result = await new FileScanner(engine, _options.Scan).ScanAsync(path, token);
        }
        catch (Exception ex) when (ex is EngineUnavailableException or ObjectDisposedException)
        {
            // Motor recarregando (atualização, mudança de configurações): tenta de novo depois.
            _engineReady = false;
            _pending.TryAdd(path, pending with { NotBefore = DateTime.UtcNow + _options.SettleDelay });
            return;
        }

        var now = DateTime.UtcNow;
        if (result.Outcome == ScanOutcome.ReadError && now - pending.FirstSeen < _options.RetryWindow && File.Exists(path))
        {
            // Ainda em uso por quem está gravando: tenta de novo com espera crescente.
            var attempts = pending.Attempts + 1;
            var wait = TimeSpan.FromSeconds(Math.Min(2 * attempts, 20));
            _pending.TryAdd(path, pending with { NotBefore = now + wait, Attempts = attempts });
            return;
        }

        _lastPath = path;
        switch (result.Outcome)
        {
            case ScanOutcome.NoThreatDetected:
                Interlocked.Increment(ref _scanned);
                break;
            case ScanOutcome.Threat:
            case ScanOutcome.Suspicious:
                Interlocked.Increment(ref _scanned);
                Interlocked.Increment(ref _detections);
                Detected?.Invoke(this, result);
                break;
            case ScanOutcome.NotScanned:
                Interlocked.Increment(ref _notScanned);
                break;
            default:
                Interlocked.Increment(ref _readErrors);
                break;
        }

        if (result.Outcome != ScanOutcome.ReadError)
        {
            Remember(path, length, writeUtc);
        }
    }

    private bool AlreadyHandled(string path, long length, DateTime writeUtc)
    {
        lock (_handledLock)
        {
            return _handled.TryGetValue(path, out var seen) && seen.Length == length && seen.WriteUtc == writeUtc;
        }
    }

    private void Remember(string path, long length, DateTime writeUtc)
    {
        lock (_handledLock)
        {
            if (_handled.Count >= MaxRemembered)
            {
                _handled.Clear();
            }

            _handled[path] = (length, writeUtc);
        }
    }

    private void SetState(RealTimeState state, string? message)
    {
        _state = state;
        _message = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Pending(DateTime FirstSeen, DateTime NotBefore, int Attempts);
}
