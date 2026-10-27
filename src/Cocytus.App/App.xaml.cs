using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Cocytus.App.Services;
using Cocytus.App.ViewModels;
using Cocytus.Core;

namespace Cocytus.App;

public partial class App : Application
{
    private AppServices? _services;
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private AppUpdateService? _updates;
    private Window? _updateWindow;
    private MainWindow? _window;
    private MainViewModel? _main;
    private bool _exiting;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var command = CommandLineOptions.Parse(e.Args);
        if (command.ApplyUpdateTarget is not null)
        {
            // Modo de instalação de uma versão nova: não abre a interface normal.
            _ = ApplyUpdateAndExitAsync(command);
            return;
        }

        _instance = SingleInstance.Acquire();
        if (!_instance.IsPrimary)
        {
            // Já existe um Cocytus aberto: entrega o pedido a ele e encerra.
            NativeMethods.AllowAnyProcessToSetForeground();
            var message = command.ScanNow
                ? new InstanceMessage(InstanceMessage.Scan, command.Paths.ToArray())
                : new InstanceMessage(InstanceMessage.Show, command.Paths.ToArray());
            if (!SingleInstance.TrySend(message, TimeSpan.FromSeconds(15)))
            {
                MessageBox.Show("O Cocytus já está aberto, mas não respondeu. Tente de novo em alguns segundos.", "Cocytus",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        try
        {
            _services = new AppServices(CocytusPaths.FromEnvironment(command.DataDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show("Não foi possível preparar a pasta de dados do Cocytus: " + ex.Message, "Cocytus",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var services = _services;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            services.Log.Error("Exceção não observada em tarefa", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            services.Log.Error("Exceção não tratada", args.ExceptionObject as Exception);
        AsyncRelayCommand.ErrorHandler = ex => ReportError(ex);

        _main = new MainViewModel(services);
        _main.ExitRequested += (_, _) => RequestExit();
        _window = new MainWindow { DataContext = _main };
        _window.Closing += OnWindowClosing;
        _window.Closed += (_, _) => ExitApplication();
        MainWindow = _window;

        _tray = new TrayIcon(ShowWindow, () => ShowPage(_main.RealTime), RequestExit);
        services.RealTimeDetected += (_, result) => Dispatcher.InvokeAsync(() => NotifyDetection(result));
        _instance.StartServer(message => Dispatcher.InvokeAsync(() => HandleMessage(message)));

        if (!command.StartInTray)
        {
            _window.Show();
        }

        if (command.ScanNow)
        {
            ShowWindow();
            _main.RequestScan(command.Paths);
        }
        else
        {
            _main.Scan.AddTargets(command.Paths);
        }

        _ = StartRealTimeAsync(services);

        _updates = new AppUpdateService(services);
        _main.SetUpdateService(_updates, ShowUpdateWindow);
        _updates.UpdateAvailable += (_, release) => ShowUpdateWindow(release);
        _updates.Start();
        if (command.JustUpdated)
        {
            var message = $"Cocytus atualizado para a versão {AppUpdateService.CurrentVersion.ToString(3)}.";
            services.Log.Info(message);
            services.History.AddEvent(Cocytus.Data.History.HistoryStore.EventUpdate, message);
            _tray.Notify("Cocytus atualizado", message, warning: false, onClick: ShowWindow);
        }
    }

    private async Task ApplyUpdateAndExitAsync(CommandLineOptions command)
    {
        await UpdateApplier.RunAsync(command);
        Shutdown();
    }

    /// <summary>Tela "Nova versão disponível" (uma por vez).</summary>
    private void ShowUpdateWindow(Cocytus.Update.AppRelease release)
    {
        if (_services is null || _updates is null)
        {
            return;
        }

        if (_updateWindow is { IsLoaded: true })
        {
            _updateWindow.Activate();
            return;
        }

        var vm = new AppUpdateViewModel(_services, _updates, release);
        var window = new Views.AppUpdateWindow { DataContext = vm };
        if (_window is { IsVisible: true })
        {
            window.Owner = _window;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        vm.CloseRequested += (_, _) => window.Close();
        vm.ExitForUpdateRequested += (_, _) =>
        {
            window.Close();
            RequestExit();
        };
        window.Closed += (_, _) => _updateWindow = null;
        _updateWindow = window;
        window.Show();
        window.Activate();
    }

    private async Task StartRealTimeAsync(AppServices services)
    {
        try
        {
            await services.ApplyRealTimeAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            services.Log.Error("Falha ao iniciar o tempo real", ex);
        }
    }

    private void HandleMessage(InstanceMessage message)
    {
        ShowWindow();
        if (message.Command == InstanceMessage.Scan && message.Paths.Length > 0)
        {
            _main?.RequestScan(message.Paths);
        }
        else if (message.Paths.Length > 0)
        {
            _main?.Scan.AddTargets(message.Paths);
        }
    }

    private void NotifyDetection(FileScanResult result)
    {
        var title = result.Outcome == ScanOutcome.Threat ? "Ameaça detectada pelo tempo real" : "Arquivo suspeito detectado pelo tempo real";
        var text = $"{Path.GetFileName(result.Path)}{Environment.NewLine}{result.ThreatName}{Environment.NewLine}Clique para revisar e decidir o que fazer.";
        _tray?.Notify(title, text, warning: true, onClick: () =>
        {
            if (_main is not null)
            {
                ShowPage(_main.RealTime);
            }
        });
    }

    private void ShowPage(PageViewModel page)
    {
        ShowWindow();
        if (_main is not null)
        {
            _main.SelectedPage = page;
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _services?.Settings.RealTimeEnabled != true)
        {
            return;
        }

        // Com o tempo real ligado, fechar a janela só a esconde: a vigilância continua na bandeja.
        e.Cancel = true;
        _window?.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.Notify("O Cocytus continua em execução",
                "A verificação em tempo real segue ativa. Para encerrar, use Sair no ícone da bandeja.", warning: false, onClick: ShowWindow);
        }
    }

    private void RequestExit()
    {
        _exiting = true;
        if (_window is not null)
        {
            _window.Close();
        }
        else
        {
            ExitApplication();
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportError(e.Exception);
        e.Handled = true;
    }

    private void ReportError(Exception ex)
    {
        _services?.Log.Error("Erro inesperado na interface", ex);
        Dialogs.Error($"Ocorreu um erro inesperado: {ex.Message}{Environment.NewLine}{Environment.NewLine}Detalhes no log: {_services?.Log.FilePath}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updates?.Dispose();
        _tray?.Dispose();
        if (_services is not null)
        {
            // Para o tempo real e encerra o clamd (o Job Object também garante isso se o processo cair).
            _services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            _services.Log.Info("Cocytus encerrado.");
        }

        _instance?.Dispose();
        base.OnExit(e);
    }
}
