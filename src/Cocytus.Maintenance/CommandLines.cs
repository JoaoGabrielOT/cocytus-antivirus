namespace Cocytus.Maintenance;

/// <summary>Interpreta linhas de comando gravadas pelo Windows (inicialização, serviços, desinstaladores).</summary>
public static class CommandLines
{
    private static readonly string[] HostExecutables = ["rundll32.exe", "regsvr32.exe", "wscript.exe", "cscript.exe", "mshta.exe"];

    /// <summary>
    /// Separa executável e argumentos. Aceita caminho entre aspas, caminho sem aspas com espaços (testando no disco
    /// os prefixos possíveis) e variáveis de ambiente. Devolve null se não houver nada utilizável.
    /// </summary>
    public static (string FileName, string Arguments)? Split(string? command, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        fileExists ??= File.Exists;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close <= 1)
            {
                return null;
            }

            return (text[1..close], text[(close + 1)..].Trim());
        }

        if (fileExists(text))
        {
            return (text, "");
        }

        // Sem aspas: o primeiro prefixo terminado em espaço que existe no disco é o executável.
        for (var i = text.IndexOf(' '); i > 0; i = text.IndexOf(' ', i + 1))
        {
            var candidate = text[..i];
            if (fileExists(candidate))
            {
                return (candidate, text[(i + 1)..].Trim());
            }
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>
    /// Arquivos que um comando de inicialização executa: o executável e, quando ele só hospeda outro arquivo
    /// (rundll32, regsvr32, wscript…), também o arquivo hospedado. Caminhos relativos ao System32 são completados.
    /// </summary>
    public static IReadOnlyList<string> ExtractFiles(string? command, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var split = Split(command, fileExists);
        if (split is not { } parts)
        {
            return [];
        }

        var files = new List<string>();
        AddResolved(files, parts.FileName, fileExists);
        var name = Path.GetFileName(parts.FileName);
        if (HostExecutables.Contains(name, StringComparer.OrdinalIgnoreCase) && parts.Arguments.Length > 0)
        {
            var hosted = FirstArgument(parts.Arguments);
            if (hosted is not null)
            {
                AddResolved(files, hosted, fileExists);
            }
        }

        return files;
    }

    /// <summary>Converte caminhos de serviço do tipo \SystemRoot\..., \??\C:\... e System32\... em caminhos completos.</summary>
    public static string NormalizeServicePath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(windows, path[@"\SystemRoot\".Length..]);
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(windows, path);
        }

        return path;
    }

    private static string? FirstArgument(string arguments)
    {
        var text = arguments.TrimStart();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        // rundll32 arquivo.dll,Funcao  /  regsvr32 /s arquivo.dll
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('/') || token.StartsWith('-'))
            {
                continue;
            }

            var comma = token.IndexOf(',');
            return comma > 0 ? token[..comma] : token;
        }

        return null;
    }

    private static void AddResolved(List<string> files, string path, Func<string, bool> fileExists)
    {
        var candidate = NormalizeServicePath(path.Trim().Trim('"'));
        if (candidate.Length == 0)
        {
            return;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var inSystem = Path.Combine(system, candidate);
            if (fileExists(inSystem))
            {
                candidate = inSystem;
            }
            else if (!Path.HasExtension(candidate) && fileExists(inSystem + ".exe"))
            {
                candidate = inSystem + ".exe";
            }
            else
            {
                return;
            }
        }

        if (!files.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(candidate);
        }
    }
}
