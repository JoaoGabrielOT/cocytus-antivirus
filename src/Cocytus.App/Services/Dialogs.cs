using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Cocytus.App.Services;

/// <summary>Caixas de diálogo e seletores do Windows.</summary>
public static class Dialogs
{
    private const string Title = "Cocytus";

    private static Window? Owner => Application.Current?.MainWindow;

    public static bool Confirm(string message, bool warning = false)
    {
        var owner = Owner;
        var icon = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var result = owner is null
            ? MessageBox.Show(message, Title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(owner, message, Title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Info(string message) => Show(message, MessageBoxImage.Information);

    public static void Error(string message) => Show(message, MessageBoxImage.Error);

    private static void Show(string message, MessageBoxImage icon)
    {
        var owner = Owner;
        if (owner is null)
        {
            MessageBox.Show(message, Title, MessageBoxButton.OK, icon);
        }
        else
        {
            MessageBox.Show(owner, message, Title, MessageBoxButton.OK, icon);
        }
    }

    public static string[] PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha os arquivos para verificar",
            Multiselect = true,
            CheckFileExists = true,
            // Atalhos (.lnk) não são resolvidos: o próprio atalho é verificado, não o destino.
            DereferenceLinks = false,
        };
        return ShowDialog(dialog) ? dialog.FileNames : [];
    }

    public static string[] PickFolders()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha as pastas para verificar",
            Multiselect = true,
        };
        return ShowDialog(dialog) ? dialog.FolderNames : [];
    }

    public static string? PickSavePath(string title, string initialDirectory, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            FileName = fileName,
            OverwritePrompt = false,
            AddExtension = false,
        };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    private static bool ShowDialog(CommonDialog dialog) => (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    /// <summary>Abre o Explorador com o item selecionado (não abre nem executa o arquivo).</summary>
    public static void RevealInExplorer(string path)
    {
        var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false });
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
    }
}
