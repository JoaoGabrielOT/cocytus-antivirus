using System.Security;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Core;
using Cocytus.Data.History;

namespace Cocytus.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private CocytusSettings _edit = new();
    private string _status = "";
    private string _contextMenuStatus = "";
    private string _startupStatus = "";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_services.IsBusy);
        DefaultsCommand = new RelayCommand(() => Load(new CocytusSettings()));
        OpenDataFolderCommand = new RelayCommand(() => Dialogs.OpenFolder(_services.Paths.DataRoot));
        OpenLogsFolderCommand = new RelayCommand(() => Dialogs.OpenFolder(_services.Paths.LogsDir));
        ReleaseEngineCommand = new AsyncRelayCommand(ReleaseEngineAsync, () => !_services.IsBusy);
        InstallContextMenuCommand = new RelayCommand(() => ChangeIntegration("Menu do botão direito adicionado.", () => WindowsIntegration.InstallContextMenu(_services.Paths)));
        RemoveContextMenuCommand = new RelayCommand(() => ChangeIntegration("Menu do botão direito removido.", WindowsIntegration.RemoveContextMenu));
        EnableStartupCommand = new RelayCommand(() => ChangeIntegration("Início com o Windows ativado.", () => WindowsIntegration.EnableStartup(_services.Paths)));
        DisableStartupCommand = new RelayCommand(() => ChangeIntegration("Início com o Windows desativado.", WindowsIntegration.DisableStartup));
        RefreshIntegration();
        Load(_services.Settings);
        if (_services.SettingsWarning is not null)
        {
            Status = _services.SettingsWarning;
        }
    }

    public override string Title => "Configurações";

    public override string Glyph => "";

    public ICommand SaveCommand { get; }

    public ICommand DefaultsCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    public ICommand ReleaseEngineCommand { get; }

    public ICommand InstallContextMenuCommand { get; }

    public ICommand RemoveContextMenuCommand { get; }

    public ICommand EnableStartupCommand { get; }

    public ICommand DisableStartupCommand { get; }

    public string ContextMenuStatus
    {
        get => _contextMenuStatus;
        private set => Set(ref _contextMenuStatus, value);
    }

    public string StartupStatus
    {
        get => _startupStatus;
        private set => Set(ref _startupStatus, value);
    }

    public string DataRoot => _services.Paths.DataRoot;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public int MaxFileSizeMb
    {
        get => _edit.MaxFileSizeMb;
        set => Change(() => _edit.MaxFileSizeMb = value);
    }

    public int Parallelism
    {
        get => _edit.Parallelism;
        set => Change(() => _edit.Parallelism = value);
    }

    public int PerFileTimeoutSeconds
    {
        get => _edit.PerFileTimeoutSeconds;
        set => Change(() => _edit.PerFileTimeoutSeconds = value);
    }

    public int ArchiveMaxScanSizeMb
    {
        get => _edit.ArchiveMaxScanSizeMb;
        set => Change(() => _edit.ArchiveMaxScanSizeMb = value);
    }

    public int ArchiveMaxFileSizeMb
    {
        get => _edit.ArchiveMaxFileSizeMb;
        set => Change(() => _edit.ArchiveMaxFileSizeMb = value);
    }

    public int ArchiveMaxRecursion
    {
        get => _edit.ArchiveMaxRecursion;
        set => Change(() => _edit.ArchiveMaxRecursion = value);
    }

    public int ArchiveMaxFiles
    {
        get => _edit.ArchiveMaxFiles;
        set => Change(() => _edit.ArchiveMaxFiles = value);
    }

    public int ArchiveMaxScanTimeSeconds
    {
        get => _edit.ArchiveMaxScanTimeSeconds;
        set => Change(() => _edit.ArchiveMaxScanTimeSeconds = value);
    }

    public bool DetectPua
    {
        get => _edit.DetectPua;
        set => Change(() => _edit.DetectPua = value);
    }

    public bool KeepEngineLoaded
    {
        get => _edit.KeepEngineLoaded;
        set => Change(() => _edit.KeepEngineLoaded = value);
    }

    public bool CheckAppUpdates
    {
        get => _edit.CheckAppUpdates;
        set => Change(() => _edit.CheckAppUpdates = value);
    }

    public override Task OnShownAsync()
    {
        Load(_services.Settings);
        RefreshIntegration();
        return Task.CompletedTask;
    }

    private void RefreshIntegration()
    {
        ContextMenuStatus = WindowsIntegration.Describe(WindowsIntegration.GetContextMenuCommand(), WindowsIntegration.ContextMenuCommand(_services.Paths));
        var startup = WindowsIntegration.GetStartupCommand();
        StartupStatus = WindowsIntegration.Describe(startup, WindowsIntegration.StartupCommand(_services.Paths))
            + (startup is not null && WindowsIntegration.StartupDisabledByUser()
                ? " Porém está desativado na inicialização (Otimização → Inicialização ou Gerenciador de Tarefas); clique em Ativar para religar."
                : "");
    }

    private void ChangeIntegration(string done, Action change)
    {
        try
        {
            change();
            _services.Log.Info(done);
            _services.History.AddEvent(HistoryStore.EventSettings, done);
            Status = done;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _services.Log.Error("Falha ao alterar a integração com o Windows", ex);
            Status = "Não foi possível alterar o registro do usuário: " + ex.Message;
        }

        RefreshIntegration();
    }

    private void Change(Action apply)
    {
        apply();
        Status = "Alterações não salvas.";
    }

    private void Load(CocytusSettings settings)
    {
        _edit = settings.Clone();
        Raise(string.Empty);
        Status = "";
    }

    private async Task SaveAsync()
    {
        var normalized = _edit.Clone().Normalize();
        // O tempo real é configurado na própria tela; aqui vale o estado atual.
        normalized.RealTimeEnabled = _services.Settings.RealTimeEnabled;
        normalized.RealTimeFolders = _services.Settings.RealTimeFolders?.ToList();
        normalized.SkippedAppVersion = _services.Settings.SkippedAppVersion;
        normalized.LastAppUpdateCheck = _services.Settings.LastAppUpdateCheck;
        var adjusted = normalized.MaxFileSizeMb != _edit.MaxFileSizeMb || normalized.Parallelism != _edit.Parallelism
            || normalized.PerFileTimeoutSeconds != _edit.PerFileTimeoutSeconds || normalized.ArchiveMaxRecursion != _edit.ArchiveMaxRecursion
            || normalized.ArchiveMaxScanSizeMb != _edit.ArchiveMaxScanSizeMb || normalized.ArchiveMaxFileSizeMb != _edit.ArchiveMaxFileSizeMb
            || normalized.ArchiveMaxFiles != _edit.ArchiveMaxFiles || normalized.ArchiveMaxScanTimeSeconds != _edit.ArchiveMaxScanTimeSeconds;
        try
        {
            await _services.SaveSettingsAsync(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Não foi possível salvar: " + ex.Message;
            return;
        }

        Load(_services.Settings);
        Status = adjusted
            ? "Salvo. Alguns valores estavam fora da faixa permitida e foram ajustados."
            : "Configurações salvas. Limites do motor valem a partir da próxima varredura.";
    }

    private async Task ReleaseEngineAsync()
    {
        await _services.ReleaseEngineAsync();
        Status = "Motor encerrado. Ele será carregado de novo na próxima varredura.";
    }
}
