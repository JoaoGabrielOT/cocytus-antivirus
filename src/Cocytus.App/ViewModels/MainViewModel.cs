using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Cocytus.App.Services;
using Cocytus.Core.Protection;

namespace Cocytus.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _shellTimer;
    private readonly List<string> _shellPaths = [];
    private PageViewModel _selectedPage;
    private string _engineStatus = "";

    public MainViewModel(AppServices services)
    {
        _services = services;
        Scan = new ScanViewModel(services);
        RealTime = new RealTimeViewModel(services);
        Update = new UpdateViewModel(services);
        Pages =
        [
            Scan,
            RealTime,
            new QuarantineViewModel(services),
            new HistoryViewModel(services),
            new OptimizationViewModel(services),
            new UninstallViewModel(services),
            Update,
            new SettingsViewModel(services),
            new AboutViewModel(services),
        ];
        _selectedPage = Scan;
        // O menu do botão direito abre uma execução por item selecionado: junta os caminhos que chegam em sequência.
        _shellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _shellTimer.Tick += (_, _) => FlushShellRequest();
        services.StateChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshEngineStatus);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        RefreshEngineStatus();
    }

    /// <summary>Encerrar de verdade (fechar a janela só esconde quando o tempo real está ativo).</summary>
    public event EventHandler? ExitRequested;

    public ICommand ExitCommand { get; }

    public ScanViewModel Scan { get; }

    public RealTimeViewModel RealTime { get; }

    public UpdateViewModel Update { get; }

    /// <summary>Liga a tela Atualização ao serviço de atualização do próprio Cocytus.</summary>
    public void SetUpdateService(AppUpdateService updates, Action<Cocytus.Update.AppRelease> showUpdate) =>
        Update.AttachAppUpdates(updates, showUpdate);

    public ObservableCollection<PageViewModel> Pages { get; }

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not null && Set(ref _selectedPage, value))
            {
                _ = ShowAsync(value);
            }
        }
    }

    public string EngineStatus
    {
        get => _engineStatus;
        private set => Set(ref _engineStatus, value);
    }

    public string ProtectionNotice => _services.RealTime?.Snapshot.State switch
    {
        null => "Tempo real desativado · só verificação sob demanda",
        RealTimeState.Active => "Tempo real ativo · verifica após a gravação, não bloqueia",
        RealTimeState.EngineUnavailable => "Tempo real sem motor (veja a tela Tempo real)",
        _ => "Tempo real iniciando…",
    };

    /// <summary>Pedido de verificação vindo do menu do botão direito (pode chegar em várias partes).</summary>
    public void RequestScan(IEnumerable<string> paths)
    {
        _shellPaths.AddRange(paths);
        _shellTimer.Stop();
        _shellTimer.Start();
    }

    private void FlushShellRequest()
    {
        _shellTimer.Stop();
        var paths = _shellPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _shellPaths.Clear();
        SelectedPage = Scan;
        Scan.StartFromShell(paths);
    }

    private async Task ShowAsync(PageViewModel page)
    {
        try
        {
            await page.OnShownAsync();
        }
        catch (Exception ex)
        {
            AsyncRelayCommand.ErrorHandler?.Invoke(ex);
        }

        RefreshEngineStatus();
    }

    private void RefreshEngineStatus()
    {
        EngineStatus = _services.EngineStatusText;
        Raise(nameof(ProtectionNotice));
    }
}
