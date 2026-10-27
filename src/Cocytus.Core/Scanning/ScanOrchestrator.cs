using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Cocytus.Core.Scanning;

public sealed record ScanOptions(long MaxFileSizeBytes, int Parallelism, TimeSpan PerFileTimeout)
{
    public static ScanOptions FromSettings(CocytusSettings settings) =>
        new((long)settings.MaxFileSizeMb * 1024 * 1024, settings.Parallelism, TimeSpan.FromSeconds(settings.PerFileTimeoutSeconds));
}

/// <summary>
/// Orquestra uma varredura: enumera os alvos numa tarefa, analisa os arquivos em N tarefas paralelas
/// (fila limitada), aplica limites de tamanho e de tempo, e consolida o relatório.
/// Nada aqui roda na thread da interface.
/// </summary>
public sealed class ScanOrchestrator
{
    public const int MaxStoredItems = 50_000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    private readonly IScanEngine _engine;
    private readonly FileScanner _scanner;
    private readonly int _parallelism;

    public ScanOrchestrator(IScanEngine engine, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Paralelismo precisa ser pelo menos 1.");
        }

        _engine = engine;
        _scanner = new FileScanner(engine, options);
        _parallelism = options.Parallelism;
    }

    public Task<ScanReport> RunAsync(IReadOnlyList<string> targets, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return RunAsync(ScanRequest.ForSelected(targets), progress, cancellationToken);
    }

    public async Task<ScanReport> RunAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targets = FileEnumerator.NormalizeTargets(request.Targets);
        var shown = request.Description ?? targets.Select(t => t.Path).ToList();
        var run = new Run(Guid.NewGuid(), request.Kind, shown, targets, request.ExcludedFolders, DateTimeOffset.Now, progress);
        run.Report(ScanPhase.StartingEngine, null, force: true);

        if (run.ScanTargets.Count == 0)
        {
            return run.Build(ScanStatus.Failed, "Nenhum arquivo ou pasta selecionado.", null);
        }

        EngineInfo engineInfo;
        try
        {
            engineInfo = await _engine.EnsureReadyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return run.Build(ScanStatus.Cancelled, "Cancelada antes de o motor ficar pronto.", null);
        }
        catch (EngineUnavailableException ex)
        {
            return run.Build(ScanStatus.Failed, "Motor indisponível: " + ex.Message, null);
        }

        var channel = Channel.CreateBounded<ScanItem>(new BoundedChannelOptions(256)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(() => ProduceAsync(run, channel.Writer, stop.Token), CancellationToken.None);
        var workers = Enumerable.Range(0, _parallelism)
            .Select(_ => Task.Run(() => ConsumeAsync(run, channel.Reader, stop), CancellationToken.None))
            .ToArray();
        var all = workers.Append(producer).ToArray();

        try
        {
            await Task.WhenAll(all);
        }
        catch (Exception) when (all.All(t => t.IsCompleted))
        {
            // As exceções são examinadas abaixo, tarefa a tarefa.
        }

        ScanStatus status;
        string? error = null;
        if (cancellationToken.IsCancellationRequested)
        {
            status = ScanStatus.Cancelled;
            error = "Cancelada pelo usuário. Os arquivos que não chegaram a ser analisados não entram na contagem.";
        }
        else
        {
            var failure = all
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            status = failure is null ? ScanStatus.Completed : ScanStatus.Failed;
            error = failure is null ? null : "Falha interna na varredura: " + failure.Message;
        }

        return run.Build(status, error, engineInfo);
    }

    private static async Task ProduceAsync(Run run, ChannelWriter<ScanItem> writer, CancellationToken token)
    {
        try
        {
            foreach (var item in FileEnumerator.Enumerate(run.ScanTargets, run.ExcludedFolders, token))
            {
                run.IncrementFound();
                await writer.WriteAsync(item, token);
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    private async Task ConsumeAsync(Run run, ChannelReader<ScanItem> reader, CancellationTokenSource stop)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(stop.Token))
            {
                run.Report(ScanPhase.Scanning, item.Path, force: false);
                var result = item.Kind switch
                {
                    ScanItemKind.File => await _scanner.ScanAsync(item.Path, stop.Token),
                    ScanItemKind.Skipped => new FileScanResult(item.Path, ScanOutcome.NotScanned, item.Reason, null, item.Detail, null, null),
                    _ => new FileScanResult(item.Path, ScanOutcome.ReadError, NotScannedReason.None, null, item.Detail, null, null),
                };
                if (result.Outcome is ScanOutcome.NoThreatDetected or ScanOutcome.Threat or ScanOutcome.Suspicious && result.Size is { } bytes)
                {
                    run.AddBytes(bytes);
                }

                run.Record(result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Erro inesperado (não de arquivo): para os demais trabalhadores e propaga.
            await stop.CancelAsync();
            throw;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Estado de uma varredura em andamento (contadores atômicos e itens não limpos).</summary>
    private sealed class Run
    {
        private readonly IProgress<ScanProgress>? _progress;
        private readonly ConcurrentQueue<FileScanResult> _items = new();
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private readonly Lock _reportLock = new();
        private int _found;
        private int _noThreat;
        private int _threats;
        private int _suspicious;
        private int _notScanned;
        private int _readErrors;
        private int _stored;
        private long _bytes;

        public Run(Guid id, ScanKind kind, IReadOnlyList<string> targets, IReadOnlyList<ScanTarget> scanTargets, IReadOnlyList<string> excludedFolders,
            DateTimeOffset startedAt, IProgress<ScanProgress>? progress)
        {
            Id = id;
            Kind = kind;
            Targets = targets;
            ScanTargets = scanTargets;
            ExcludedFolders = excludedFolders;
            StartedAt = startedAt;
            _progress = progress;
        }

        public Guid Id { get; }

        public ScanKind Kind { get; }

        /// <summary>O que o relatório mostra como alvos.</summary>
        public IReadOnlyList<string> Targets { get; }

        public IReadOnlyList<ScanTarget> ScanTargets { get; }

        public IReadOnlyList<string> ExcludedFolders { get; }

        public DateTimeOffset StartedAt { get; }

        public void IncrementFound() => Interlocked.Increment(ref _found);

        public void AddBytes(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public void Record(FileScanResult result)
        {
            switch (result.Outcome)
            {
                case ScanOutcome.NoThreatDetected:
                    Interlocked.Increment(ref _noThreat);
                    return;
                case ScanOutcome.Threat:
                    Interlocked.Increment(ref _threats);
                    break;
                case ScanOutcome.Suspicious:
                    Interlocked.Increment(ref _suspicious);
                    break;
                case ScanOutcome.NotScanned:
                    Interlocked.Increment(ref _notScanned);
                    break;
                default:
                    Interlocked.Increment(ref _readErrors);
                    break;
            }

            if (Interlocked.Increment(ref _stored) <= MaxStoredItems)
            {
                _items.Enqueue(result);
            }
        }

        public void Report(ScanPhase phase, string? currentPath, bool force)
        {
            if (_progress is null)
            {
                return;
            }

            lock (_reportLock)
            {
                if (!force && _sinceReport.Elapsed < ProgressInterval)
                {
                    return;
                }

                _sinceReport.Restart();
            }

            _progress.Report(Snapshot(phase, currentPath));
        }

        private ScanProgress Snapshot(ScanPhase phase, string? currentPath)
        {
            var noThreat = Volatile.Read(ref _noThreat);
            var threats = Volatile.Read(ref _threats);
            var suspicious = Volatile.Read(ref _suspicious);
            var notScanned = Volatile.Read(ref _notScanned);
            var readErrors = Volatile.Read(ref _readErrors);
            return new ScanProgress(phase, Volatile.Read(ref _found), noThreat + threats + suspicious + notScanned + readErrors,
                noThreat, threats, suspicious, notScanned, readErrors, Interlocked.Read(ref _bytes), currentPath);
        }

        public ScanReport Build(ScanStatus status, string? error, EngineInfo? engine)
        {
            Report(ScanPhase.Finished, null, force: true);
            var items = _items
                .OrderBy(i => SeverityOrder(i.Outcome))
                .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ScanReport(Id, Targets, StartedAt, DateTimeOffset.Now, status, error, engine,
                _noThreat, _threats, _suspicious, _notScanned, _readErrors, Interlocked.Read(ref _bytes),
                items, _stored > MaxStoredItems)
            {
                Kind = Kind,
            };
        }

        private static int SeverityOrder(ScanOutcome outcome) => outcome switch
        {
            ScanOutcome.Threat => 0,
            ScanOutcome.Suspicious => 1,
            ScanOutcome.ReadError => 2,
            ScanOutcome.NotScanned => 3,
            _ => 4,
        };
    }
}
