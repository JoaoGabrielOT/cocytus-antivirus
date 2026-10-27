using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Data.History;
using Cocytus.Engines.ClamAv;
using Cocytus.Update;

namespace Cocytus.App.ViewModels;

public sealed class DatabaseRowViewModel(ClamAvDatabaseFile file)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public string Name => file.FileName;

    public int Version => file.Version;

    public string Signatures => file.Signatures.ToString("N0", PtBr);

    public string DateText => file.BuildTime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr) ?? "—";
}

public sealed class UpdateViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _status = "";
    private string _logText = "";
    private AppUpdateService? _appUpdates;
    private Action<AppRelease>? _showUpdate;
    private bool _checkingApp;
    private string _appStatus = "";

    public UpdateViewModel(AppServices services)
    {
        _services = services;
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !IsRunning && !_services.IsBusy && _services.EngineInstalled);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        CheckAppCommand = new AsyncRelayCommand(CheckAppAsync, () => _appUpdates is not null && !_checkingApp);
        OpenRepositoryCommand = new RelayCommand(() =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GitHubReleaseClient.RepositoryUrl + "/releases") { UseShellExecute = true })?.Dispose());
    }

    public ICommand CheckAppCommand { get; }

    public ICommand OpenRepositoryCommand { get; }

    public string AppVersionText => $"Versão instalada: {AppUpdateService.CurrentVersion.ToString(3)} · releases em {GitHubReleaseClient.RepositoryUrl}";

    public string AppStatus
    {
        get => _appStatus;
        private set => Set(ref _appStatus, value);
    }

    public void AttachAppUpdates(AppUpdateService updates, Action<AppRelease> showUpdate)
    {
        _appUpdates = updates;
        _showUpdate = showUpdate;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CheckAppAsync()
    {
        if (_appUpdates is null)
        {
            return;
        }

        _checkingApp = true;
        CommandManager.InvalidateRequerySuggested();
        AppStatus = "Consultando o GitHub…";
        try
        {
            var release = await _appUpdates.CheckAsync(manual: true, CancellationToken.None);
            if (release is null)
            {
                AppStatus = $"Você já tem a versão mais recente ({AppUpdateService.CurrentVersion.ToString(3)}).";
            }
            else
            {
                AppStatus = $"Versão {release.Version.ToString(3)} disponível.";
                _showUpdate?.Invoke(release);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            AppStatus = "Não foi possível consultar o GitHub: " + ex.Message;
        }
        finally
        {
            _checkingApp = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public override string Title => "Atualização";

    public override string Glyph => "";

    public ObservableCollection<DatabaseRowViewModel> Databases { get; } = [];

    public ICommand UpdateCommand { get; }

    public ICommand CancelCommand { get; }

    public string EngineText => _services.EngineInstalled
        ? $"Motor: {_services.EngineVersionText} em {_services.Paths.ClamAvDir}"
        : $"Motor ClamAV não encontrado em {_services.Paths.ClamAvDir}. Sem ele não há verificação nem atualização.";

    public string DatabaseDir => _services.Paths.ClamAvDatabaseDir;

    public string NetworkNotice =>
        $"A atualização baixa as bases oficiais e gratuitas do ClamAV de {FreshclamUpdater.Server}. "
        + "Nenhum arquivo, hash ou caminho do seu computador é enviado. O freshclam confere a assinatura digital de cada base "
        + "e testa a base nova antes de substituir a anterior; se algo falhar, as bases anteriores continuam em uso.";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            Set(ref _isRunning, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    public override async Task OnShownAsync()
    {
        var files = await Task.Run(() => ClamAvDatabase.ReadAll(_services.Paths.ClamAvDatabaseDir));
        ShowDatabases(files);
        if (!IsRunning && string.IsNullOrEmpty(Status))
        {
            Status = files.Count == 0
                ? "Nenhuma base instalada. Clique em Atualizar bases para baixar (cerca de 100 MB ou mais na primeira vez)."
                : "Bases instaladas listadas abaixo.";
        }

        Raise(nameof(EngineText));
    }

    private void ShowDatabases(IReadOnlyList<ClamAvDatabaseFile> files)
    {
        Databases.Clear();
        foreach (var file in files)
        {
            Databases.Add(new DatabaseRowViewModel(file));
        }
    }

    private async Task UpdateAsync()
    {
        if (!_services.TryBeginBusy())
        {
            Dialogs.Info("Já há uma varredura ou atualização em andamento.");
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _log.Clear();
        LogText = "";
        Status = $"Atualizando a partir de {FreshclamUpdater.Server}…";
        _services.Log.Info("Atualização das bases iniciada.");
        try
        {
            var output = new Progress<string>(line =>
            {
                _log.AppendLine(line);
                LogText = _log.ToString();
            });
            var updater = _services.CreateUpdater();
            var token = _cts.Token;
            var result = await Task.Run(() => updater.UpdateAsync(output, token), CancellationToken.None);
            ShowDatabases(result.Databases);
            Status = result.Message;
            _services.History.AddEvent(result.Success ? HistoryStore.EventUpdate : HistoryStore.EventError,
                $"{result.Message} (freshclam código {result.ExitCode})");
            _services.Log.Info($"Atualização: {result.Message} (código {result.ExitCode})");
            if (result.Changed)
            {
                // O clamd recarrega as bases ao subir de novo.
                await _services.ReleaseEngineAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Atualização cancelada. As bases anteriores foram mantidas.";
            _services.History.AddEvent(HistoryStore.EventUpdate, Status);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            _services.EndBusy();
        }
    }
}
