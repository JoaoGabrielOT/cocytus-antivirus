using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Core.Scanning;
using Cocytus.Data.History;
using Cocytus.Data.Quarantine;

namespace Cocytus.App.ViewModels;

public sealed class QuarantineRowViewModel(QuarantineItem item)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public QuarantineItem Item { get; } = item;

    public string DateText => Item.QuarantinedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr);

    public string OriginalPath => Item.OriginalPath;

    public string FileName => Path.GetFileName(Item.OriginalPath);

    public string Reason => Item.Reason;

    public string EngineText => Item.DatabaseVersion is null ? Item.Engine : $"{Item.Engine} (daily {Item.DatabaseVersion})";

    public string SizeText => ScanOrchestrator.FormatBytes(Item.Size);

    public string Sha256 => Item.Sha256;
}

public sealed class QuarantineViewModel : PageViewModel
{
    private readonly AppServices _services;
    private QuarantineRowViewModel? _selected;
    private string _status = "";

    public QuarantineViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(OnShownAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => Selected is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null);
        OpenFolderCommand = new RelayCommand(() => Dialogs.OpenFolder(_services.Quarantine.FolderPath));
    }

    public override string Title => "Quarentena";

    public override string Glyph => "";

    public ObservableCollection<QuarantineRowViewModel> Items { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public QuarantineRowViewModel? Selected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public override async Task OnShownAsync()
    {
        var items = await Task.Run(() => _services.Quarantine.List());
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new QuarantineRowViewModel(item));
        }

        Status = Items.Count == 0
            ? "A quarentena está vazia."
            : $"{Items.Count} item(ns) na quarentena. Os arquivos ficam cifrados em {_services.Quarantine.FolderPath}.";
    }

    private async Task RestoreAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var message = $"Restaurar \"{row.OriginalPath}\"?{Environment.NewLine}{Environment.NewLine}"
            + $"Este arquivo foi detectado como: {row.Reason}.{Environment.NewLine}"
            + "Ao restaurar, ele volta a ficar disponível no disco e pode ser aberto ou executado. Restaure só se tiver certeza de que é seguro.";
        if (!Dialogs.Confirm(message, warning: true))
        {
            return;
        }

        string? destination = null;
        while (true)
        {
            try
            {
                var restored = await Task.Run(() => _services.Quarantine.Restore(row.Item.Id, destination));
                // Restaurado por decisão do usuário: o tempo real não avisa de novo sobre este arquivo.
                _services.RealTime?.MarkHandled(restored);
                _services.History.AddEvent(HistoryStore.EventRestore, $"Restaurado da quarentena ({row.Reason}).", restored);
                _services.Log.Info($"Restaurado: {row.Item.Id} -> {restored}");
                Dialogs.Info($"Arquivo restaurado em:{Environment.NewLine}{restored}");
                break;
            }
            catch (RestoreConflictException conflict)
            {
                if (!Dialogs.Confirm($"{conflict.Message}{Environment.NewLine}{Environment.NewLine}Deseja escolher outro nome ou local para restaurar?"))
                {
                    return;
                }

                var folder = Path.GetDirectoryName(conflict.TargetPath) ?? "";
                var name = Path.GetFileNameWithoutExtension(conflict.TargetPath) + " (restaurado)" + Path.GetExtension(conflict.TargetPath);
                destination = Dialogs.PickSavePath("Restaurar como", folder, name);
                if (destination is null)
                {
                    return;
                }
            }
            catch (QuarantineException ex)
            {
                _services.History.AddEvent(HistoryStore.EventError, "Falha ao restaurar: " + ex.Message, row.OriginalPath);
                _services.Log.Error("Falha ao restaurar", ex);
                Dialogs.Error(ex.Message);
                return;
            }
        }

        await OnShownAsync();
    }

    private async Task DeleteAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        if (!Dialogs.Confirm($"Excluir definitivamente a cópia de \"{row.FileName}\" guardada na quarentena?{Environment.NewLine}{Environment.NewLine}Esta ação não pode ser desfeita.", warning: true)
            || !Dialogs.Confirm("Confirma a exclusão definitiva? Depois disso não será possível restaurar o arquivo.", warning: true))
        {
            return;
        }

        try
        {
            await Task.Run(() => _services.Quarantine.Delete(row.Item.Id));
            _services.History.AddEvent(HistoryStore.EventDelete, $"Excluído definitivamente da quarentena ({row.Reason}).", row.OriginalPath);
            _services.Log.Info($"Excluído da quarentena: {row.Item.Id} ({row.OriginalPath})");
        }
        catch (Exception ex) when (ex is QuarantineException or IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Falha ao excluir da quarentena", ex);
            Dialogs.Error("Não foi possível excluir: " + ex.Message);
        }

        await OnShownAsync();
    }
}
