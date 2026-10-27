using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Core;
using Cocytus.Core.Scanning;
using Cocytus.Data.History;

namespace Cocytus.App.ViewModels;

public sealed class ScanRecordRowViewModel(ScanRecord record)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public ScanRecord Record { get; } = record;

    public string DateText => Record.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr);

    public string StatusText => Texts.Status(Record.Status);

    public string TargetsText => string.Join("; ", Record.Targets);

    public int Analyzed => Record.NoThreat + Record.Threats + Record.Suspicious;

    public int Threats => Record.Threats;

    public int Suspicious => Record.Suspicious;

    public int NotScanned => Record.NotScanned;

    public int ReadErrors => Record.ReadErrors;

    public string DurationText => $"{(Record.FinishedAt - Record.StartedAt).TotalSeconds.ToString("0.#", PtBr)} s";

    public string EngineText => Record.EngineName is null
        ? "Motor não iniciado"
        : $"{Record.EngineName} {Record.EngineVersion} · base daily {Record.DatabaseVersion ?? "?"}"
            + (Record.DatabaseDate is { } d ? $" de {d.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr)}" : "");

    public string DetailText =>
        $"{StatusText}. Analisados: {Analyzed} (sem detecção {Record.NoThreat}, ameaças {Record.Threats}, suspeitos {Record.Suspicious}). "
        + $"Não analisados: {Record.NotScanned}. Erros de leitura: {Record.ReadErrors}. Volume: {ScanOrchestrator.FormatBytes(Record.BytesScanned)}."
        + (Record.Error is null ? "" : " " + Record.Error)
        + (Record.ItemsTruncated ? " A lista de itens foi truncada." : "");
}

public sealed class EventRowViewModel(HistoryEvent ev)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public string DateText => ev.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", PtBr);

    public string Kind => ev.Kind;

    public string Message => ev.Message;

    public string Path => ev.Path ?? "";
}

public sealed class HistoryViewModel : PageViewModel
{
    private readonly AppServices _services;
    private ScanRecordRowViewModel? _selected;

    public HistoryViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(OnShownAsync);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => Scans.Count > 0 || Events.Count > 0);
    }

    public override string Title => "Histórico";

    public override string Glyph => "";

    public ObservableCollection<ScanRecordRowViewModel> Scans { get; } = [];

    public ObservableCollection<ResultRowViewModel> SelectedItems { get; } = [];

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand ClearCommand { get; }

    public ScanRecordRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                _ = LoadItemsAsync(value);
            }
        }
    }

    public override async Task OnShownAsync()
    {
        var (scans, events) = await Task.Run(() => (_services.History.ListScans(), _services.History.ListEvents()));
        Scans.Clear();
        foreach (var scan in scans)
        {
            Scans.Add(new ScanRecordRowViewModel(scan));
        }

        Events.Clear();
        foreach (var ev in events)
        {
            Events.Add(new EventRowViewModel(ev));
        }

        Selected = Scans.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadItemsAsync(ScanRecordRowViewModel? row)
    {
        SelectedItems.Clear();
        if (row is null)
        {
            return;
        }

        var items = await Task.Run(() => _services.History.GetScanItems(row.Record.Id));
        if (!ReferenceEquals(row, Selected))
        {
            return;
        }

        foreach (var item in items)
        {
            SelectedItems.Add(new ResultRowViewModel(item));
        }
    }

    private async Task ClearAsync()
    {
        if (!Dialogs.Confirm("Apagar todo o histórico de varreduras e eventos? A quarentena não é afetada.", warning: true))
        {
            return;
        }

        await Task.Run(_services.History.Clear);
        _services.Log.Info("Histórico apagado pelo usuário.");
        await OnShownAsync();
    }
}
