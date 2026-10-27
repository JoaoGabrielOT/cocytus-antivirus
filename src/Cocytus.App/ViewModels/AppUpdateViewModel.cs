using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Core.Scanning;
using Cocytus.Update;

namespace Cocytus.App.ViewModels;

/// <summary>Tela "Nova versão do Cocytus disponível": atualizar agora, depois ou pular a versão.</summary>
public sealed class AppUpdateViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly AppServices _services;
    private readonly AppUpdateService _updates;
    private CancellationTokenSource? _cts;
    private bool _working;
    private double _progress;
    private string _status = "";

    public AppUpdateViewModel(AppServices services, AppUpdateService updates, AppRelease release)
    {
        _services = services;
        _updates = updates;
        Release = release;
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !_working);
        LaterCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty), () => !_working);
        SkipCommand = new AsyncRelayCommand(SkipAsync, () => !_working);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _working && _cts is { IsCancellationRequested: false });
        OpenPageCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(Release.PageUrl) { UseShellExecute = true })?.Dispose());
    }

    public event EventHandler? CloseRequested;

    /// <summary>O instalador foi iniciado: o Cocytus precisa fechar.</summary>
    public event EventHandler? ExitForUpdateRequested;

    public AppRelease Release { get; }

    public ICommand UpdateCommand { get; }

    public ICommand LaterCommand { get; }

    public ICommand SkipCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand OpenPageCommand { get; }

    public string Heading => $"Cocytus {Release.Version.ToString(3)} disponível";

    public string Details
    {
        get
        {
            var text = $"Você usa a versão {AppUpdateService.CurrentVersion.ToString(3)}.";
            if (Release.PublishedAt is { } date)
            {
                text += $" Publicada em {date.ToLocalTime().ToString("dd/MM/yyyy", PtBr)}.";
            }

            if (Release.PackageSize > 0)
            {
                text += $" Download de {ScanOrchestrator.FormatBytes(Release.PackageSize)}.";
            }

            return text;
        }
    }

    public string Notes => string.IsNullOrWhiteSpace(Release.Notes) ? "Sem notas para esta versão." : Release.Notes.Trim();

    public string BusyWarning => _services.IsBusy
        ? "Há uma verificação ou atualização de bases em andamento; ela será interrompida quando o Cocytus fechar para instalar."
        : "";

    public bool IsWorking
    {
        get => _working;
        private set
        {
            if (Set(ref _working, value))
            {
                Raise(nameof(IsIdle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsIdle => !IsWorking;

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private async Task UpdateAsync()
    {
        IsWorking = true;
        _cts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();
        try
        {
            Status = "Baixando…";
            var progress = new Progress<double>(value =>
            {
                Progress = value * 100;
                Status = $"Baixando… {value:P0}";
            });
            var folder = await _updates.DownloadAndExtractAsync(Release, progress, _cts.Token);
            Status = "Pacote conferido (SHA-256). Fechando o Cocytus para instalar…";
            if (!_updates.LaunchInstaller(folder, Release))
            {
                Status = "Instalação cancelada: o aviso de administrador do Windows foi recusado.";
                IsWorking = false;
                return;
            }

            ExitForUpdateRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Download cancelado.";
            IsWorking = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _services.Log.Error("Falha na atualização do Cocytus", ex);
            Status = "Não foi possível atualizar: " + ex.Message;
            IsWorking = false;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task SkipAsync()
    {
        await _updates.SkipAsync(Release);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
