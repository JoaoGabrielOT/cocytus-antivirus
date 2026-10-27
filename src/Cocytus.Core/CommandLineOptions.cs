namespace Cocytus.Core;

/// <summary>
/// Argumentos do Cocytus.exe:
///   --verificar &lt;caminhos…&gt;  verifica os caminhos logo ao abrir (usado pelo menu do botão direito)
///   --bandeja                  inicia minimizado na bandeja (usado ao iniciar com o Windows)
///   --dados &lt;pasta&gt;          usa outra pasta de dados (equivale a COCYTUS_DATA)
///   &lt;caminhos…&gt;              sem --verificar: só entram na lista de alvos
///   --aplicar-atualizacao &lt;pasta&gt; --pid &lt;n&gt;  (interno) instala a versão desta pasta na pasta indicada
///   --atualizado               (interno) aberto logo depois de uma atualização
/// </summary>
public sealed record CommandLineOptions(IReadOnlyList<string> Paths, bool ScanNow, bool StartInTray, string? DataDir)
{
    /// <summary>Pasta do programa a atualizar com os arquivos da pasta deste executável.</summary>
    public string? ApplyUpdateTarget { get; init; }

    /// <summary>Processo do Cocytus antigo, que precisa fechar antes da cópia.</summary>
    public int? WaitForProcessId { get; init; }

    public bool JustUpdated { get; init; }

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var paths = new List<string>();
        var scan = false;
        var tray = false;
        string? data = null;
        string? applyTarget = null;
        int? waitPid = null;
        var updated = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--verificar":
                    scan = true;
                    break;
                case "--bandeja":
                    tray = true;
                    break;
                case "--aplicar-atualizacao":
                    if (i + 1 < args.Count)
                    {
                        applyTarget = ToFullPath(FixQuotedRoot(args[++i]));
                    }

                    break;
                case "--pid":
                    if (i + 1 < args.Count && int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                    {
                        waitPid = pid;
                    }

                    break;
                case "--atualizado":
                    updated = true;
                    break;
                case "--dados":
                    if (i + 1 < args.Count)
                    {
                        data = ToFullPath(FixQuotedRoot(args[++i]));
                    }

                    break;
                default:
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        paths.Add(ToFullPath(FixQuotedRoot(arg)));
                    }

                    break;
            }
        }

        return new CommandLineOptions(paths, scan, tray, data)
        {
            ApplyUpdateTarget = applyTarget,
            WaitForProcessId = waitPid,
            JustUpdated = updated,
        };
    }

    /// <summary>
    /// Caminhos relativos são resolvidos aqui, na pasta atual de quem chamou: se outro Cocytus já estiver aberto,
    /// o pedido é repassado a ele, que tem outra pasta atual.
    /// </summary>
    private static string ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// O Explorer passa a raiz de uma unidade como "C:\" entre aspas; a regra de linha de comando do Windows
    /// transforma \" em aspas literais e o argumento chega como C:". Aqui ele volta a ser C:\.
    /// </summary>
    private static string FixQuotedRoot(string arg) => arg.EndsWith('"') ? arg[..^1] + "\\" : arg;
}
