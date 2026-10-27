using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Cocytus.App.Services;
using Cocytus.Core;
using Cocytus.Core.Protection;

namespace Cocytus.App.ViewModels;

public sealed class RealTimeDetectionRow : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private string _state = "Aguardando decisão";
    private bool _handled;

    public RealTimeDetectionRow(FileScanResult result, DateTimeOffset at)
    {
        Result = result;
        At = at;
    }

    public FileScanResult Result { get; }

    public DateTimeOffset At { get; }

    public string DateText => At.ToString("dd/MM/yyyy HH:mm:ss", PtBr);

    public ScanOutcome Outcome => Result.Outcome;

    public string OutcomeText => Texts.Outcome(Result.Outcome);

    public string FileName => Path.GetFileName(Result.Path);

    public string Folder => Path.GetDirectoryName(Result.Path) ?? "";

    public string Name => Result.ThreatName ?? "";

    public bool CanQuarantine => !_handled;

    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public void MarkHandled(string state)
    {
        _handled = true;
        State = state;
        Raise(nameof(CanQuarantine));
    }
}

public sealed class RealTimeViewModel : PageViewModel
{
    private const int MaxRows = 300;
    private readonly AppServices _services;
    private RealTimeSnapshot? _snapshot;
    private RealTimeDetectionRow? _selectedDetection;
    private string? _selectedFolder;
    private string _status = "";

    public RealTimeViewModel(AppServices services)
    {
        _services = services;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveFolderAsync, () => SelectedFolder is not null);
        DefaultFoldersCommand = new AsyncRelayCommand(DefaultFoldersAsync, () => _services.Settings.RealTimeFolders is not null);
        QuarantineCommand = new AsyncRelayCommand(QuarantineAsync, () => SelectedDetection is { CanQuarantine: true });
        RevealCommand = new RelayCommand(() => Dialogs.RevealInExplorer(SelectedDetection!.Result.Path), () => SelectedDetection is not null);
        DismissCommand = new RelayCommand(Dismiss, () => SelectedDetection is not null);
        services.RealTimeDetected += (_, result) =>
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => AddDetection(result));
        services.StateChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(Refresh);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
    }

    public override string Title => "Tempo real";

    public override string Glyph => "";

    public ObservableCollection<string> Folders { get; } = [];

    public ObservableCollection<RealTimeDetectionRow> Detections { get; } = [];

    public ICommand ToggleCommand { get; }

    public ICommand AddFolderCommand { get; }

    public ICommand RemoveFolderCommand { get; }

    public ICommand DefaultFoldersCommand { get; }

    public ICommand QuarantineCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand DismissCommand { get; }

    public bool Enabled => _services.Settings.RealTimeEnabled;

    public string ToggleText => Enabled ? "Desativar tempo real" : "Ativar tempo real";

    public string StateText => _snapshot?.State switch
    {
        null => "Desativado",
        RealTimeState.Starting => "Iniciando (carregando o motor)…",
        RealTimeState.Active => "Ativo",
        RealTimeState.EngineUnavailable => "Sem motor: aguardando",
        _ => "Parado",
    };

    public string MessageText => _snapshot?.Message ?? "";

    public long Scanned => _snapshot?.Scanned ?? 0;

    public long DetectionsCount => _snapshot?.Detections ?? 0;

    public long NotScanned => _snapshot?.NotScanned ?? 0;

    public long ReadErrors => _snapshot?.ReadErrors ?? 0;

    public long Overflows => _snapshot?.Overflows ?? 0;

    public long QueueFull => _snapshot?.QueueFull ?? 0;

    public int Pending => _snapshot?.Pending ?? 0;

    public string LastPath => _snapshot?.LastPath ?? "";

    public string FoldersHint => _services.Settings.RealTimeFolders is null
        ? "Pastas padrão do usuário (Downloads, Área de Trabalho, Documentos e Temp)."
        : "Lista personalizada.";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public RealTimeDetectionRow? SelectedDetection
    {
        get => _selectedDetection;
        set
        {
            Set(ref _selectedDetection, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            Set(ref _selectedFolder, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public override Task OnShownAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        _snapshot = _services.RealTime?.Snapshot;
        var folders = _services.RealTimeFolders;
        if (!folders.SequenceEqual(Folders, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Clear();
            foreach (var folder in folders)
            {
                Folders.Add(folder);
            }
        }

        Raise(string.Empty);
    }

    private void AddDetection(FileScanResult result)
    {
        Detections.Insert(0, new RealTimeDetectionRow(result, DateTimeOffset.Now));
        while (Detections.Count > MaxRows)
        {
            Detections.RemoveAt(Detections.Count - 1);
        }

        Refresh();
    }

    private async Task ToggleAsync()
    {
        var settings = _services.Settings.Clone();
        settings.RealTimeEnabled = !settings.RealTimeEnabled;
        await _services.SaveSettingsAsync(settings);
        Status = settings.RealTimeEnabled
            ? "Tempo real ativado. O motor fica carregado enquanto o Cocytus estiver aberto (inclusive na bandeja)."
            : "Tempo real desativado.";
        Refresh();
    }

    private async Task AddFolderAsync()
    {
        var picked = Dialogs.PickFolders();
        if (picked.Length == 0)
        {
            return;
        }

        var settings = _services.Settings.Clone();
        settings.RealTimeFolders = _services.RealTimeFolders.Concat(picked).ToList();
        await _services.SaveSettingsAsync(settings);
        Refresh();
    }

    private async Task RemoveFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is null)
        {
            return;
        }

        var settings = _services.Settings.Clone();
        settings.RealTimeFolders = _services.RealTimeFolders.Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        await _services.SaveSettingsAsync(settings);
        Refresh();
    }

    private async Task DefaultFoldersAsync()
    {
        var settings = _services.Settings.Clone();
        settings.RealTimeFolders = null;
        await _services.SaveSettingsAsync(settings);
        Refresh();
    }

    private async Task QuarantineAsync()
    {
        var row = SelectedDetection;
        if (row is null || !row.CanQuarantine)
        {
            return;
        }

        var message = $"Mover para a quarentena?{Environment.NewLine}{Environment.NewLine}{row.Result.Path}{Environment.NewLine}"
            + $"Detecção: {row.Name} ({row.OutcomeText}){Environment.NewLine}{Environment.NewLine}"
            + "O arquivo sai do local original e fica guardado cifrado. Você pode restaurá-lo depois na tela Quarentena.";
        if (!Dialogs.Confirm(message, warning: true))
        {
            return;
        }

        var result = (await _services.QuarantineAsync([row.Result], "ClamAV", _services.DailyDatabaseVersion)).Single();
        if (result.Error is not null)
        {
            Dialogs.Error("Não foi possível mover para a quarentena: " + result.Error);
            return;
        }

        row.MarkHandled(result.StillPresent ? "Copiado; original ainda presente" : "Em quarentena");
        CommandManager.InvalidateRequerySuggested();
    }

    private void Dismiss()
    {
        if (SelectedDetection is { } row)
        {
            Detections.Remove(row);
        }
    }
}
