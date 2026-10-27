using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Core.Scanning;
using Cocytus.Data.History;
using Cocytus.Maintenance;

namespace Cocytus.App.ViewModels;

public sealed class AppRow(InstalledApp app)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public InstalledApp App { get; } = app;

    public string Name => App.Name;

    public string Publisher => App.Publisher ?? "";

    public string Version => App.Version ?? "";

    public string InstalledText => App.InstallDate?.ToString("dd/MM/yyyy", PtBr) ?? "";

    public string SizeText => App.SizeBytes is { } size ? ScanOrchestrator.FormatBytes(size) : "";

    public string KindText => App.KindText;

    public string ScopeText => App.ScopeText;
}

public sealed class LeftoverRow(LeftoverCandidate candidate) : ObservableObject
{
    private bool _isChecked = true;
    private string _state = "";

    public LeftoverCandidate Candidate { get; } = candidate;

    public string KindText => Candidate.Kind == LeftoverKind.Folder ? "Pasta" : "Registro";

    public string Path => Candidate.Path;

    public string Reason => Candidate.Reason + (Candidate.NeedsAdmin ? " · pede administrador" : "");

    public string SizeText => Candidate.SizeBytes is { } size ? ScanOrchestrator.FormatBytes(size) : "";

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public string State
    {
        get => _state;
        set => Set(ref _state, value);
    }
}

public sealed class UninstallViewModel : PageViewModel
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan GraceAfterExit = TimeSpan.FromMinutes(2);
    private readonly AppServices _services;
    private readonly ObservableCollection<AppRow> _apps = [];
    private AppRow? _selected;
    private string _search = "";
    private bool _showStore = true;
    private bool _working;
    private bool _loaded;
    private string _status = "";
    private string _leftoversTitle = "";
    private CancellationTokenSource? _waitCts;

    public UninstallViewModel(AppServices services)
    {
        _services = services;
        Apps = CollectionViewSource.GetDefaultView(_apps);
        Apps.Filter = Matches;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !_working);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !_working && Selected?.App.CanUninstall == true);
        OpenLocationCommand = new RelayCommand(
            () => Dialogs.OpenFolder(Selected!.App.InstallLocation!),
            () => Selected?.App.InstallLocation is { } location && Directory.Exists(location));
        StopWaitingCommand = new RelayCommand(() => _waitCts?.Cancel(), () => _waitCts is { IsCancellationRequested: false });
        RemoveLeftoversCommand = new AsyncRelayCommand(RemoveLeftoversAsync, () => !_working && Leftovers.Any(l => l.IsChecked && l.State.Length == 0));
        DismissLeftoversCommand = new RelayCommand(() =>
        {
            Leftovers.Clear();
            Raise(nameof(HasLeftovers));
        }, () => !_working);
    }

    public override string Title => "Desinstalar apps";

    public override string Glyph => "";

    public ICollectionView Apps { get; }

    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];

    public bool HasLeftovers => Leftovers.Count > 0;

    public ICommand RefreshCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand StopWaitingCommand { get; }

    public ICommand RemoveLeftoversCommand { get; }

    public ICommand DismissLeftoversCommand { get; }

    public AppRow? Selected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Apps.Refresh();
                Raise(nameof(CountText));
            }
        }
    }

    public bool ShowStoreApps
    {
        get => _showStore;
        set
        {
            if (Set(ref _showStore, value))
            {
                Apps.Refresh();
                Raise(nameof(CountText));
            }
        }
    }

    public string CountText => $"{_apps.Count(a => Matches(a))} de {_apps.Count} app(s)";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string LeftoversTitle
    {
        get => _leftoversTitle;
        private set => Set(ref _leftoversTitle, value);
    }

    public override async Task OnShownAsync()
    {
        if (!_loaded && !_working)
        {
            await LoadAsync();
        }
    }

    private bool Matches(object item) =>
        item is AppRow row
        && (ShowStoreApps || row.App.Kind != AppKind.Store)
        && (SearchText.Length == 0
            || row.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || row.Publisher.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));

    private void BeginWork()
    {
        _working = true;
        CommandManager.InvalidateRequerySuggested();
    }

    private void EndWork()
    {
        _working = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadAsync()
    {
        BeginWork();
        try
        {
            await LoadCoreAsync();
        }
        finally
        {
            EndWork();
        }
    }

    private async Task LoadCoreAsync()
    {
        Status = "Carregando a lista de apps…";
        var desktop = await Task.Run(() => InstalledApps.ListDesktop());
        IReadOnlyList<InstalledApp> store = [];
        string? storeError = null;
        try
        {
            store = await Task.Run(() => StoreApps.List());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            storeError = ex.Message;
        }

        _apps.Clear();
        foreach (var app in desktop.Concat(store).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _apps.Add(new AppRow(app));
        }

        _loaded = true;
        Raise(nameof(CountText));
        Status = storeError is null ? "" : "Não foi possível listar os apps da Microsoft Store: " + storeError;
    }

    private async Task UninstallAsync()
    {
        if (Selected is not { } row || !row.App.CanUninstall)
        {
            return;
        }

        var app = row.App;
        var message = app.Kind == AppKind.Store
            ? $"Desinstalar {app.Name}?{Environment.NewLine}{Environment.NewLine}O app da Microsoft Store será removido da sua conta neste computador."
            : $"Desinstalar {app.Name}?{Environment.NewLine}{Environment.NewLine}O desinstalador do próprio programa será aberto: siga as instruções dele. "
                + "Depois que ele terminar, o Cocytus procura pastas e chaves de registro que sobraram e mostra a lista antes de remover qualquer coisa.";
        if (!Dialogs.Confirm(message))
        {
            return;
        }

        Leftovers.Clear();
        Raise(nameof(HasLeftovers));
        BeginWork();
        try
        {
            if (app.Kind == AppKind.Store)
            {
                await UninstallStoreAsync(app);
            }
            else
            {
                await UninstallDesktopAsync(app);
            }
        }
        finally
        {
            EndWork();
        }
    }

    private async Task UninstallStoreAsync(InstalledApp app)
    {
        Status = $"Removendo {app.Name}…";
        string? error;
        try
        {
            error = await StoreApps.RemoveAsync(app.PackageFullName!, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            Status = $"Não foi possível remover {app.Name}: {error}";
            _services.Log.Info(Status);
            return;
        }

        var done = $"{app.Name} (Microsoft Store) desinstalado.";
        _services.Log.Info(done);
        _services.History.AddEvent(HistoryStore.EventUninstall, done);
        await LoadCoreAsync();
        Status = done;
    }

    private async Task UninstallDesktopAsync(InstalledApp app)
    {
        if (InstalledApps.UninstallCommand(app) is not { } command)
        {
            Status = "O comando de desinstalação registrado por este programa não pôde ser interpretado.";
            return;
        }

        _services.Log.Info($"Desinstalação de {app.Name}: \"{command.FileName}\" {command.Arguments}");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Status = "Desinstalação cancelada (o aviso de administrador foi recusado).";
            return;
        }
        catch (Win32Exception ex)
        {
            Status = $"Não foi possível abrir o desinstalador ({command.FileName}): {ex.Message}";
            return;
        }

        _waitCts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();
        bool removed;
        try
        {
            Status = $"Aguardando o desinstalador de {app.Name}… (siga as instruções na janela dele)";
            removed = await WaitForRemovalAsync(app, process, _waitCts.Token);
        }
        finally
        {
            process?.Dispose();
            _waitCts.Dispose();
            _waitCts = null;
            CommandManager.InvalidateRequerySuggested();
        }

        if (!removed)
        {
            Status = $"{app.Name} continua instalado (o desinstalador foi cancelado, ainda não terminou ou pede reiniciar). O Cocytus não removeu nada.";
            await LoadCoreAsync();
            return;
        }

        var done = $"{app.Name} desinstalado.";
        _services.Log.Info(done);
        _services.History.AddEvent(HistoryStore.EventUninstall, done);
        await LoadCoreAsync();
        Status = done + " Procurando restos…";
        var remaining = _apps.Select(r => r.App).ToList();
        var candidates = await Task.Run(() => LeftoverFinder.Find(app, remaining));
        if (candidates.Count == 0)
        {
            Status = done + " Nenhum resto encontrado.";
            return;
        }

        foreach (var candidate in candidates)
        {
            Leftovers.Add(new LeftoverRow(candidate));
        }

        LeftoversTitle = $"Restos de {app.Name}";
        Raise(nameof(HasLeftovers));
        Status = done + $" {candidates.Count} resto(s) encontrado(s): revise a lista abaixo.";
    }

    /// <summary>Espera a entrada sumir do registro. Depois que o processo sai, ainda espera um pouco (desinstaladores que se relançam).</summary>
    private static async Task<bool> WaitForRemovalAsync(InstalledApp app, Process? process, CancellationToken token)
    {
        DateTime? exitedAt = null;
        try
        {
            while (true)
            {
                if (!InstalledApps.StillInstalled(app))
                {
                    return true;
                }

                if (process is not null && exitedAt is null && process.HasExited)
                {
                    exitedAt = DateTime.UtcNow;
                }

                if (exitedAt is { } at && DateTime.UtcNow - at > GraceAfterExit)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException)
        {
            return !InstalledApps.StillInstalled(app);
        }
    }

    private async Task RemoveLeftoversAsync()
    {
        var rows = Leftovers.Where(l => l.IsChecked && l.State.Length == 0).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var list = string.Join(Environment.NewLine, rows.Take(12).Select(r => $"• {r.KindText}: {r.Path}"));
        if (rows.Count > 12)
        {
            list += Environment.NewLine + $"• … e mais {rows.Count - 12}";
        }

        if (!Dialogs.Confirm($"Remover {rows.Count} resto(s)?{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}"
            + "Pastas vão para a Lixeira. Chaves de registro são exportadas para um arquivo .reg (cópia de segurança) antes de serem apagadas. "
            + "Itens de Program Files, ProgramData e HKLM pedem confirmação de administrador do Windows.", warning: true))
        {
            return;
        }

        BeginWork();
        try
        {
            foreach (var row in rows)
            {
                row.State = "Removendo…";
                try
                {
                    if (row.Candidate.Kind == LeftoverKind.Folder)
                    {
                        await ShellOperations.SendFolderToRecycleBinAsync(row.Path);
                        row.State = Directory.Exists(row.Path) ? "Não removida" : "Na Lixeira";
                    }
                    else
                    {
                        var backup = await Task.Run(() => LeftoverRemover.BackupAndDeleteKey(row.Path, _services.RegistryBackupDir));
                        row.State = backup is null ? "Cancelado" : "Removida (cópia: " + Path.GetFileName(backup) + ")";
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                    or System.Security.SecurityException or ArgumentException or Win32Exception)
                {
                    row.State = "Falhou: " + ex.Message;
                }

                _services.Log.Info($"Resto {row.Path}: {row.State}");
            }

            var removed = rows.Count(r => r.State.StartsWith("Na Lixeira", StringComparison.Ordinal) || r.State.StartsWith("Removida", StringComparison.Ordinal));
            _services.History.AddEvent(HistoryStore.EventUninstall, $"{LeftoversTitle}: {removed} de {rows.Count} removido(s).");
            Status = $"{removed} de {rows.Count} resto(s) removido(s). Cópias das chaves de registro em {_services.RegistryBackupDir}.";
        }
        finally
        {
            EndWork();
        }
    }
}
