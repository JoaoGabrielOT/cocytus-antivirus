using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Cocytus.Core;
using Cocytus.Update;

namespace Cocytus.App.Services;

/// <summary>
/// Modo de instalação (Cocytus.exe --aplicar-atualizacao): roda a partir da pasta da versão nova, espera o Cocytus
/// antigo fechar, copia os arquivos para a pasta do programa e abre o Cocytus atualizado.
/// </summary>
public static class UpdateApplier
{
    public static async Task RunAsync(CommandLineOptions command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = command.ApplyUpdateTarget!;
        var paths = CocytusPaths.FromEnvironment(command.DataDir);
        Directory.CreateDirectory(paths.LogsDir);
        var logFile = Path.Combine(paths.LogsDir, "atualizacao.log");
        void Log(string message) => TryAppend(logFile, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");

        var window = CreateWindow(out var status);
        window.Show();
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var backup = Path.Combine(paths.DataRoot, "atualizacoes", "copia-anterior");
        Log($"Instalando {AppUpdateService.CurrentVersion.ToString(3)} de {source} em {target}.");
        try
        {
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A pasta de origem e a de destino são a mesma.");
            }

            status.Text = "Esperando o Cocytus fechar…";
            await Task.Run(() => WaitForExit(command.WaitForProcessId));
            status.Text = "Copiando os arquivos novos…";
            var count = await Task.Run(() => UpdateInstaller.Apply(source, target, backup, Log));
            Log($"Concluído: {count} arquivo(s).");
            status.Text = "Abrindo o Cocytus atualizado…";
            StartUpdatedApp(target, command.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log("Falha: " + ex);
            window.Close();
            MessageBox.Show($"Não foi possível instalar a atualização: {ex.Message}{Environment.NewLine}{Environment.NewLine}"
                + $"A versão anterior foi mantida. Detalhes em {logFile}", "Cocytus", MessageBoxButton.OK, MessageBoxImage.Error);
            TryStart(Path.Combine(target, UpdateInstaller.ExecutableName), null);
            return;
        }

        window.Close();
    }

    private static void WaitForExit(int? processId)
    {
        if (processId is not { } id)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                throw new InvalidOperationException("O Cocytus antigo não fechou em 2 minutos.");
            }
        }
        catch (ArgumentException)
        {
            // Já fechou.
        }
    }

    private static void StartUpdatedApp(string target, string? dataDir)
    {
        var exe = Path.Combine(target, UpdateInstaller.ExecutableName);
        if (Environment.IsPrivilegedProcess)
        {
            // Este instalador está elevado: abre pelo Explorador para o Cocytus não herdar o administrador.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
            return;
        }

        var args = new List<string> { "--atualizado" };
        if (dataDir is not null)
        {
            args.Add("--dados");
            args.Add(dataDir);
        }

        TryStart(exe, args);
    }

    private static void TryStart(string exe, IReadOnlyList<string>? args)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var arg in args ?? [])
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // O usuário abre manualmente.
        }
    }

    private static Window CreateWindow(out TextBlock status)
    {
        status = new TextBlock { Text = "Preparando…", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Atualizando o Cocytus para a versão {AppUpdateService.CurrentVersion.ToString(3)}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
        var window = new Window
        {
            Title = "Cocytus",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = panel,
            Topmost = true,
        };
        if (Application.Current?.TryFindResource("CyBackground") is System.Windows.Media.Brush background)
        {
            window.Background = background;
        }

        if (Application.Current?.TryFindResource("CyText") is System.Windows.Media.Brush foreground)
        {
            window.Foreground = foreground;
        }

        return window;
    }

    private static void TryAppend(string file, string text)
    {
        try
        {
            File.AppendAllText(file, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log é melhor esforço.
        }
    }
}
