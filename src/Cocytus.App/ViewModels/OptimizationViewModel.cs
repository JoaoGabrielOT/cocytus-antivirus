using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Cocytus.App.Services;
using Cocytus.Core.Scanning;
using Cocytus.Data.History;
using Cocytus.Maintenance;

namespace Cocytus.App.ViewModels;

public sealed class DriveRow(DriveUsage drive)
{
    public string Name => string.IsNullOrWhiteSpace(drive.Label) ? drive.Name : $"{drive.Name} ({drive.Label})";

    public double UsedPercent => drive.UsedPercent;

    public string Text => $"{ScanOrchestrator.FormatBytes(drive.FreeBytes)} livres de {ScanOrchestrator.FormatBytes(drive.TotalBytes)} · {drive.UsedPercent:0}% em uso";
}

public sealed class ProcessRow(ProcessUsage usage)
{
    public string Name => usage.Count > 1 ? $"{usage.Name} ({usage.Count})" : usage.Name;

    public string MemoryText => ScanOrchestrator.FormatBytes(usage.WorkingSetBytes);

    public string CpuText => usage.CpuPercent.ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) + " %";
}

public sealed class CleanupRow(CleanupCategory category) : ObservableObject
{
    private bool _isChecked = true;
    private string _estimate = "Ainda não analisado";

    public CleanupCategory Category { get; } = category;

    public string Name => Category.Name;

    public string Description => Category.Description;

    public CleanupEstimate? Result { get; private set; }

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public string Estimate
    {
        get => _estimate;
        private set => Set(ref _estimate, value);
    }

    public void SetEstimate(CleanupEstimate? estimate)
    {
        Result = estimate;
        Estimate = estimate is null ? "Ainda não analisado" : $"{estimate.Files} arquivo(s) · {ScanOrchestrator.FormatBytes(estimate.Bytes)}";
    }
}

public sealed class StartupRow(StartupItem item)
{
    public StartupItem Item { get; } = item;

    public string Name => Item.Name;

    public string Command => Item.Command;

    public string Origin => Item.Origin;

    public string StateText => Item.Enabled ? "Ativado" : "Desativado";

    public bool Enabled => Item.Enabled;

    public string AdminText => Item.Scope == StartupScope.AllUsers ? "Pede administrador" : "";
}

public sealed class OptimizationViewModel : PageViewModel
{
    private readonly AppServices _services;
    private bool _working;
    private string _memoryText = "";
    private double _memoryPercent;
    private string _usageStatus = "";
    private string _cleanupStatus = "";
    private string _recycleText = "";
    private string _startupStatus = "";
    private StartupRow? _selectedStartup;

    public OptimizationViewModel(AppServices services)
    {
        _services = services;
        foreach (var category in TempCleaner.DefaultCategories())
        {
            Categories.Add(new CleanupRow(category));
        }

        RefreshUsageCommand = new AsyncRelayCommand(RefreshUsageAsync, () => !_working);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !_working);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !_working && Categories.Any(c => c.IsChecked && c.Result is { Files: > 0 }));
        EmptyRecycleCommand = new RelayCommand(EmptyRecycleBin, () => !_working);
        RefreshStartupCommand = new RelayCommand(RefreshStartup, () => !_working);
        EnableStartupCommand = new AsyncRelayCommand(() => SetStartupAsync(true), () => !_working && SelectedStartup is { Enabled: false });
        DisableStartupCommand = new AsyncRelayCommand(() => SetStartupAsync(false), () => !_working && SelectedStartup is { Enabled: true });
        RevealStartupCommand = new RelayCommand(
            () => Dialogs.RevealInExplorer(SelectedStartup!.Item.FilePath!),
            () => SelectedStartup?.Item.FilePath is { } path && File.Exists(path));
    }

    public override string Title => "Otimização";

    public override string Glyph => "";

    public ObservableCollection<DriveRow> Drives { get; } = [];

    public ObservableCollection<ProcessRow> TopMemory { get; } = [];

    public ObservableCollection<ProcessRow> TopCpu { get; } = [];

    public ObservableCollection<CleanupRow> Categories { get; } = [];

    public ObservableCollection<StartupRow> StartupItems { get; } = [];

    public ICommand RefreshUsageCommand { get; }

    public ICommand AnalyzeCommand { get; }

    public ICommand CleanCommand { get; }

    public ICommand EmptyRecycleCommand { get; }

    public ICommand RefreshStartupCommand { get; }

    public ICommand EnableStartupCommand { get; }

    public ICommand DisableStartupCommand { get; }

    public ICommand RevealStartupCommand { get; }

    public string MemoryText
    {
        get => _memoryText;
        private set => Set(ref _memoryText, value);
    }

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set => Set(ref _memoryPercent, value);
    }

    public string UsageStatus
    {
        get => _usageStatus;
        private set => Set(ref _usageStatus, value);
    }

    public string CleanupStatus
    {
        get => _cleanupStatus;
        private set => Set(ref _cleanupStatus, value);
    }

    public string RecycleText
    {
        get => _recycleText;
        private set => Set(ref _recycleText, value);
    }

    public string StartupStatus
    {
        get => _startupStatus;
        private set => Set(ref _startupStatus, value);
    }

    public StartupRow? SelectedStartup
    {
        get => _selectedStartup;
        set
        {
            Set(ref _selectedStartup, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public override async Task OnShownAsync()
    {
        RefreshStartup();
        RefreshRecycleBin();
        await RefreshUsageAsync();
    }

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

    private async Task RefreshUsageAsync()
    {
        UsageStatus = "Medindo…";
        var snapshot = await SystemUsage.CaptureAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Drives.Clear();
        foreach (var drive in snapshot.Drives)
        {
            Drives.Add(new DriveRow(drive));
        }

        var memory = snapshot.Memory;
        MemoryPercent = memory.LoadPercent;
        MemoryText = $"{ScanOrchestrator.FormatBytes((long)memory.UsedBytes)} em uso de {ScanOrchestrator.FormatBytes((long)memory.TotalBytes)} · {memory.LoadPercent}%";
        TopMemory.Clear();
        foreach (var process in snapshot.Processes.OrderByDescending(p => p.WorkingSetBytes).Take(10))
        {
            TopMemory.Add(new ProcessRow(process));
        }

        TopCpu.Clear();
        foreach (var process in snapshot.Processes.OrderByDescending(p => p.CpuPercent).Take(10))
        {
            TopCpu.Add(new ProcessRow(process));
        }

        UsageStatus = $"Medido às {snapshot.At:HH:mm:ss}. CPU: média de 1 segundo, somando todos os núcleos (100% = computador inteiro).";
    }

    private async Task AnalyzeAsync()
    {
        BeginWork();
        try
        {
            CleanupStatus = "Analisando…";
            var rows = Categories.ToList();
            var estimates = await Task.Run(() => rows.Select(r => TempCleaner.Analyze(r.Category, CancellationToken.None)).ToList());
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].SetEstimate(estimates[i]);
            }

            var total = estimates.Sum(e => e.Bytes);
            CleanupStatus = $"Pode liberar até {ScanOrchestrator.FormatBytes(total)}. Marque o que deseja limpar e clique em Limpar.";
            RefreshRecycleBin();
        }
        finally
        {
            EndWork();
        }
    }

    private async Task CleanAsync()
    {
        var rows = Categories.Where(c => c.IsChecked && c.Result is { Files: > 0 }).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var list = string.Join(Environment.NewLine, rows.Select(r => $"• {r.Name}: {r.Estimate}"));
        if (!Dialogs.Confirm($"Apagar definitivamente estes arquivos?{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}"
            + "Eles não vão para a Lixeira. Arquivos em uso são mantidos.", warning: true))
        {
            return;
        }

        BeginWork();
        try
        {
            CleanupStatus = "Limpando…";
            var results = await Task.Run(() => rows.Select(r => (Row: r, Result: TempCleaner.Clean(r.Category, CancellationToken.None))).ToList());
            var deleted = results.Sum(r => r.Result.Deleted);
            var freed = results.Sum(r => r.Result.BytesFreed);
            var skipped = results.Sum(r => r.Result.Skipped);
            var summary = $"{deleted} arquivo(s) apagado(s), {ScanOrchestrator.FormatBytes(freed)} liberados."
                + (skipped > 0 ? $" {skipped} mantido(s) por estarem em uso ou sem permissão." : "");
            CleanupStatus = summary;
            _services.Log.Info("Limpeza: " + summary + string.Concat(results.SelectMany(r => r.Result.SkippedSamples).Select(x => Environment.NewLine + "  " + x)));
            _services.History.AddEvent(HistoryStore.EventOptimization, "Limpeza de temporários: " + summary);
            var estimates = await Task.Run(() => rows.Select(r => TempCleaner.Analyze(r.Category, CancellationToken.None)).ToList());
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].SetEstimate(estimates[i]);
            }
        }
        finally
        {
            EndWork();
        }
    }

    private void RefreshRecycleBin()
    {
        try
        {
            var (items, bytes) = RecycleBin.Query();
            RecycleText = items == 0 ? "A Lixeira está vazia." : $"{items} item(ns) na Lixeira · {ScanOrchestrator.FormatBytes(bytes)}";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            RecycleText = "Não foi possível consultar a Lixeira: " + ex.Message;
        }
    }

    private void EmptyRecycleBin()
    {
        RefreshRecycleBin();
        if (!Dialogs.Confirm($"Esvaziar a Lixeira de todas as unidades?{Environment.NewLine}{Environment.NewLine}{RecycleText}{Environment.NewLine}{Environment.NewLine}"
            + "Os itens são excluídos definitivamente e não podem ser recuperados pelo Cocytus.", warning: true))
        {
            return;
        }

        try
        {
            var owner = Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            RecycleBin.Empty(owner);
            _services.Log.Info("Lixeira esvaziada.");
            _services.History.AddEvent(HistoryStore.EventOptimization, "Lixeira esvaziada.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            Dialogs.Error("Não foi possível esvaziar a Lixeira: " + ex.Message);
        }

        RefreshRecycleBin();
    }

    private void RefreshStartup()
    {
        var selected = SelectedStartup?.Item;
        StartupItems.Clear();
        foreach (var item in new StartupManager().List().OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            StartupItems.Add(new StartupRow(item));
        }

        SelectedStartup = selected is null ? null : StartupItems.FirstOrDefault(r => r.Item.ApprovedKey == selected.ApprovedKey && r.Item.ApprovedValue == selected.ApprovedValue);
        var disabled = StartupItems.Count(r => !r.Enabled);
        StartupStatus = $"{StartupItems.Count} item(ns), {disabled} desativado(s).";
    }

    private async Task SetStartupAsync(bool enable)
    {
        if (SelectedStartup is not { } row)
        {
            return;
        }

        BeginWork();
        try
        {
            var item = row.Item;
            if (item.Scope == StartupScope.AllUsers)
            {
                StartupStatus = "Item de todos os usuários: confirme no aviso de administrador do Windows.";
            }

            var done = await Task.Run(() => new StartupManager().SetEnabled(item, enable));
            if (!done)
            {
                StartupStatus = "Alteração cancelada (o aviso de administrador foi recusado).";
                return;
            }

            var message = $"{(enable ? "Ativado" : "Desativado")} na inicialização: {item.Name} ({item.Origin}).";
            _services.Log.Info(message);
            _services.History.AddEvent(HistoryStore.EventOptimization, message, item.FilePath);
            RefreshStartup();
            StartupStatus = message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            StartupStatus = "Não foi possível alterar: " + ex.Message;
        }
        finally
        {
            EndWork();
        }
    }
}
