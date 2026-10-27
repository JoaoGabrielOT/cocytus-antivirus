using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Core;
using Cocytus.Core.Scanning;
using Cocytus.Maintenance;

namespace Cocytus.App.ViewModels;

public sealed class ResultRowViewModel : ObservableObject
{
    private string _state = "";

    public ResultRowViewModel(FileScanResult result)
    {
        Result = result;
    }

    public FileScanResult Result { get; }

    public ScanOutcome Outcome => Result.Outcome;

    public string OutcomeText => Texts.Outcome(Result.Outcome);

    public string Path => Result.Path;

    public string FileName => System.IO.Path.GetFileName(Result.Path);

    public string Folder => System.IO.Path.GetDirectoryName(Result.Path) ?? "";

    public string Name => Result.ThreatName ?? Texts.Reason(Result.Reason);

    public string Detail => Result.Detail ?? "";

    public string SizeText => Result.Size is { } size ? ScanOrchestrator.FormatBytes(size) : "";

    public bool CanQuarantine => (Result.Outcome is ScanOutcome.Threat or ScanOutcome.Suspicious) && string.IsNullOrEmpty(State);

    /// <summary>Ação já feita sobre o item nesta sessão (ex.: "Em quarentena").</summary>
    public string State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(CanQuarantine));
            }
        }
    }
}

public sealed class ScanViewModel : PageViewModel
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _phaseText = "Escolha o tipo de verificação e clique em Iniciar verificação.";
    private ScanKind _mode = ScanKind.Quick;
    private string _drivesText = "";
    private string _currentPath = "";
    private ScanProgress? _progress;
    private ScanReport? _report;
    private string? _selectedTarget;
    private ResultRowViewModel? _selectedResult;

    public ScanViewModel(AppServices services)
    {
        _services = services;
        AddFilesCommand = new RelayCommand(AddFiles, () => !IsRunning);
        AddFoldersCommand = new RelayCommand(AddFolders, () => !IsRunning);
        RemoveTargetCommand = new RelayCommand(() => Targets.Remove(SelectedTarget!), () => !IsRunning && SelectedTarget is not null);
        ClearTargetsCommand = new RelayCommand(Targets.Clear, () => !IsRunning && Targets.Count > 0);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && (Mode != ScanKind.Selected || Targets.Count > 0) && !_services.IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning && _cts is { IsCancellationRequested: false });
        QuarantineCommand = new AsyncRelayCommand(QuarantineSelectedAsync, () => !IsRunning && SelectedRows.Any(r => r.CanQuarantine));
        RevealCommand = new RelayCommand(() => Dialogs.RevealInExplorer(SelectedResult!.Path), () => SelectedResult is not null);
    }

    public override string Title => "Verificar";

    public override string Glyph => "";

    public ObservableCollection<string> Targets { get; } = [];

    /// <summary>Tipo escolhido: rápida, completa ou selecionada.</summary>
    public ScanKind Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(IsQuickMode));
                Raise(nameof(IsFullMode));
                Raise(nameof(IsSelectedMode));
                if (value == ScanKind.Full)
                {
                    RefreshDrives();
                }

                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsQuickMode
    {
        get => Mode == ScanKind.Quick;
        set
        {
            if (value)
            {
                Mode = ScanKind.Quick;
            }
        }
    }

    public bool IsFullMode
    {
        get => Mode == ScanKind.Full;
        set
        {
            if (value)
            {
                Mode = ScanKind.Full;
            }
        }
    }

    public bool IsSelectedMode
    {
        get => Mode == ScanKind.Selected;
        set
        {
            if (value)
            {
                Mode = ScanKind.Selected;
            }
        }
    }

    /// <summary>Unidades que a verificação completa vai percorrer.</summary>
    public string DrivesText
    {
        get => _drivesText;
        private set => Set(ref _drivesText, value);
    }

    public ObservableCollection<ResultRowViewModel> Results { get; } = [];

    /// <summary>Linhas selecionadas na grade (atualizado pela View).</summary>
    public IList<ResultRowViewModel> SelectedRows { get; set; } = [];

    public ICommand AddFilesCommand { get; }

    public ICommand AddFoldersCommand { get; }

    public ICommand RemoveTargetCommand { get; }

    public ICommand ClearTargetsCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand QuarantineCommand { get; }

    public ICommand RevealCommand { get; }

    public string? SelectedTarget
    {
        get => _selectedTarget;
        set => Set(ref _selectedTarget, value);
    }

    public ResultRowViewModel? SelectedResult
    {
        get => _selectedResult;
        set => Set(ref _selectedResult, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                Raise(nameof(IsIdle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsIdle => !IsRunning;

    public string PhaseText
    {
        get => _phaseText;
        private set => Set(ref _phaseText, value);
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set => Set(ref _currentPath, value);
    }

    public int Found => _progress?.Found ?? 0;

    public int Processed => _progress?.Processed ?? 0;

    public int NoThreat => _progress?.NoThreat ?? 0;

    public int Threats => _progress?.Threats ?? 0;

    public int Suspicious => _progress?.Suspicious ?? 0;

    public int NotScanned => _progress?.NotScanned ?? 0;

    public int ReadErrors => _progress?.ReadErrors ?? 0;

    public string BytesText => ScanOrchestrator.FormatBytes(_progress?.BytesScanned ?? 0);

    public bool HasReport => _report is not null;

    public string SummaryTitle => _report is null ? "" : BuildSummaryTitle(_report);

    public string SummaryText => _report is null ? "" : BuildSummaryText(_report);

    public string EngineText => _report?.Engine is { } e
        ? $"{e.Name} {e.Version} · base daily {e.DatabaseVersion ?? "?"}" + (e.DatabaseDate is { } d ? $" de {d.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr)}" : "")
        : "";

    public string LimitationsText => _report?.Engine?.Limitations ?? "";

    public string Disclaimer => Texts.NoThreatDisclaimer;

    public string ResultsHeader => _report is null
        ? "Resultados"
        : Results.Count == 0
            ? "Resultados: nenhum item para revisar"
            : $"Resultados para revisar ({Results.Count}{(_report.ItemsTruncated ? "+, lista truncada" : "")})";

    public void AddTargets(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Targets.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                Targets.Add(path);
                added = true;
            }
        }

        if (added)
        {
            Mode = ScanKind.Selected;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Pedido vindo do menu do botão direito: troca a lista de alvos e já inicia.</summary>
    public void StartFromShell(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (IsRunning)
        {
            AddTargets(paths);
            PhaseText = "Já há uma verificação em andamento; os itens do menu foram adicionados à lista para a próxima.";
            return;
        }

        Targets.Clear();
        AddTargets(paths);
        Mode = ScanKind.Selected;
        if (StartCommand.CanExecute(null))
        {
            StartCommand.Execute(null);
        }
        else
        {
            PhaseText = "Há uma atualização em andamento. Clique em Iniciar verificação quando ela terminar.";
        }
    }

    private void AddFiles() => AddTargets(Dialogs.PickFiles());

    private void AddFolders() => AddTargets(Dialogs.PickFolders());

    private void Cancel()
    {
        _cts?.Cancel();
        PhaseText = "Cancelando… aguardando os arquivos em análise terminarem.";
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshDrives()
    {
        var drives = ScanLocations.FixedDrives();
        DrivesText = drives.Count == 0 ? "Nenhuma unidade fixa encontrada." : string.Join("   ", drives);
    }

    private async Task StartAsync()
    {
        var mode = Mode;
        IReadOnlyList<string> drives = [];
        if (mode == ScanKind.Full)
        {
            drives = ScanLocations.FixedDrives();
            RefreshDrives();
            if (drives.Count == 0)
            {
                Dialogs.Error("Nenhuma unidade fixa encontrada.");
                return;
            }

            var question = $"A verificação completa percorre todos os arquivos de {string.Join(", ", drives)} e pode levar horas. "
                + $"O computador continua utilizável, mas fica mais lento durante a verificação.{Environment.NewLine}{Environment.NewLine}Iniciar?";
            if (!Dialogs.Confirm(question))
            {
                return;
            }
        }

        if (!_services.TryBeginBusy())
        {
            Dialogs.Info("Já há uma varredura ou atualização em andamento.");
            return;
        }

        _cts = new CancellationTokenSource();
        _report = null;
        Results.Clear();
        RaiseReport();
        IsRunning = true;
        PhaseText = mode == ScanKind.Quick ? "Levantando programas em execução e itens de inicialização…" : "Preparando…";
        var settings = _services.Settings;
        try
        {
            var excluded = _services.ScanExclusions;
            var selected = Targets.ToList();
            var request = mode switch
            {
                ScanKind.Quick => await Task.Run(() => ScanLocations.Quick(ScanLocations.CollectQuickSources(), excluded)),
                ScanKind.Full => ScanLocations.Full(drives, excluded),
                _ => ScanRequest.ForSelected(selected),
            };
            _services.Log.Info($"Varredura iniciada ({mode}): {string.Join(" | ", request.Description ?? selected)}");
            var engine = await _services.GetEngineAsync();
            var orchestrator = new ScanOrchestrator(engine, ScanOptions.FromSettings(settings));
            var progress = new Progress<ScanProgress>(OnProgress);
            var token = _cts.Token;
            var report = await Task.Run(() => orchestrator.RunAsync(request, progress, token), CancellationToken.None);
            _report = report;
            foreach (var item in report.Items)
            {
                Results.Add(new ResultRowViewModel(item));
            }

            PhaseText = BuildSummaryTitle(report);
            CurrentPath = "";
            _progress = new ScanProgress(ScanPhase.Finished, report.Processed, report.Processed, report.NoThreat, report.Threats,
                report.Suspicious, report.NotScanned, report.ReadErrors, report.BytesScanned, null);
            RaiseCounters();
            RaiseReport();
            _services.Log.Info($"Varredura {report.Status}: {report.NoThreat} sem detecção, {report.Threats} ameaças, {report.Suspicious} suspeitos, "
                + $"{report.NotScanned} não analisados, {report.ReadErrors} erros. {report.Error}");
            try
            {
                await Task.Run(() => _services.History.SaveScan(report));
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                _services.Log.Error("Falha ao gravar o histórico", ex);
                Dialogs.Error("A varredura terminou, mas não foi possível gravar o histórico: " + ex.Message);
            }

            if (report.Status == ScanStatus.Failed && report.Error is not null)
            {
                Dialogs.Error(report.Error);
            }

            if (!settings.KeepEngineLoaded)
            {
                await _services.ReleaseEngineIfIdleAsync();
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            _services.EndBusy();
        }
    }

    private void OnProgress(ScanProgress progress)
    {
        _progress = progress;
        PhaseText = progress.Phase switch
        {
            ScanPhase.StartingEngine => "Carregando o motor ClamAV e as bases (pode levar até um minuto na primeira vez)…",
            ScanPhase.Scanning when _cts?.IsCancellationRequested == true => "Cancelando…",
            ScanPhase.Scanning => "Verificando…",
            _ => PhaseText,
        };
        if (progress.CurrentPath is not null)
        {
            CurrentPath = progress.CurrentPath;
        }

        RaiseCounters();
    }

    private void RaiseCounters()
    {
        foreach (var name in new[] { nameof(Found), nameof(Processed), nameof(NoThreat), nameof(Threats), nameof(Suspicious), nameof(NotScanned), nameof(ReadErrors), nameof(BytesText) })
        {
            Raise(name);
        }
    }

    private void RaiseReport()
    {
        foreach (var name in new[] { nameof(HasReport), nameof(SummaryTitle), nameof(SummaryText), nameof(EngineText), nameof(LimitationsText), nameof(ResultsHeader) })
        {
            Raise(name);
        }
    }

    private static string BuildSummaryTitle(ScanReport report) => report.Status switch
    {
        ScanStatus.Failed => "A varredura falhou",
        ScanStatus.Cancelled => "Varredura cancelada (resultado parcial)",
        _ when report.Threats > 0 => $"{report.Threats} ameaça(s) detectada(s)",
        _ when report.Suspicious > 0 => $"{report.Suspicious} item(ns) suspeito(s)",
        _ when report.NotScanned + report.ReadErrors > 0 => "Nenhuma ameaça detectada nos arquivos analisados, mas há itens não analisados",
        _ => "Nenhuma ameaça detectada pelo ClamAV",
    };

    private static string BuildSummaryText(ScanReport report)
    {
        var sb = new StringBuilder();
        var duration = report.FinishedAt - report.StartedAt;
        sb.Append(PtBr, $"{report.Analyzed} arquivo(s) analisado(s) pelo motor em {duration.TotalSeconds:0.#} s ({ScanOrchestrator.FormatBytes(report.BytesScanned)}). ");
        sb.Append(PtBr, $"Sem detecção: {report.NoThreat}. Ameaças: {report.Threats}. Suspeitos: {report.Suspicious}. ");
        sb.Append(PtBr, $"Não analisados: {report.NotScanned}. Erros de leitura: {report.ReadErrors}.");
        if (report.NotScanned + report.ReadErrors > 0)
        {
            sb.Append(" Itens não analisados ou com erro de leitura não foram verificados e não contam como limpos.");
        }

        if (report.Error is not null)
        {
            sb.Append(' ').Append(report.Error);
        }

        return sb.ToString();
    }

    private async Task QuarantineSelectedAsync()
    {
        var rows = SelectedRows.Where(r => r.CanQuarantine).ToList();
        if (rows.Count == 0 || _report is null)
        {
            return;
        }

        var list = string.Join(Environment.NewLine, rows.Take(8).Select(r => "• " + r.Path));
        if (rows.Count > 8)
        {
            list += Environment.NewLine + $"• … e mais {rows.Count - 8}";
        }

        var message = $"Mover {rows.Count} arquivo(s) para a quarentena?{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}"
            + "O arquivo sai do local original e fica guardado cifrado (não pode ser aberto nem executado por engano). "
            + "Você pode restaurá-lo depois na tela Quarentena.";
        if (!Dialogs.Confirm(message, warning: true))
        {
            return;
        }

        var engine = _report.Engine;
        var results = await _services.QuarantineAsync(rows.Select(r => r.Result).ToList(), engine?.Name ?? "ClamAV", engine?.DatabaseVersion);
        var failures = new List<string>();
        var stillPresent = new List<string>();
        foreach (var (item, error, present) in results)
        {
            var row = rows.First(r => ReferenceEquals(r.Result, item));
            if (error is not null)
            {
                failures.Add($"{item.Path}: {error}");
                continue;
            }

            row.State = present ? "Copiado; original ainda presente" : "Em quarentena";
            if (present)
            {
                stillPresent.Add(item.Path);
            }
        }

        var ok = rows.Count - failures.Count;
        var summary = $"{ok} de {rows.Count} arquivo(s) movido(s) para a quarentena.";
        if (stillPresent.Count > 0)
        {
            summary += $"{Environment.NewLine}{Environment.NewLine}Em {stillPresent.Count} caso(s) o original ainda aparece no disco porque outro programa o mantém aberto; ele será removido quando esse programa o fechar.";
        }

        if (failures.Count > 0)
        {
            summary += $"{Environment.NewLine}{Environment.NewLine}Falhas:{Environment.NewLine}" + string.Join(Environment.NewLine, failures);
            Dialogs.Error(summary);
        }
        else
        {
            Dialogs.Info(summary);
        }

        CommandManager.InvalidateRequerySuggested();
    }
}
