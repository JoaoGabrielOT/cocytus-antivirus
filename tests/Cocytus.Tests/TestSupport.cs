using System.Diagnostics;
using System.Text;
using Cocytus.Core;

namespace Cocytus.Tests;

/// <summary>Pasta temporária descartável. Fica em COCYTUS_TEST_TMP (dentro do projeto) quando definida.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        var root = Environment.GetEnvironmentVariable("COCYTUS_TEST_TMP");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocytus-tests");
        }

        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.GetFullPath(System.IO.Path.Combine([Path, .. parts]));

    public string WriteFile(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Melhor esforço: o diretório-raiz de testes pode ser limpo depois.
        }
    }
}

/// <summary>
/// Motor falso, só para testes do orquestrador. Reage a marcadores no conteúdo:
/// MALWARE-FALSO, SUSPEITO-FALSO, LIMITE-FALSO, ERRO-MOTOR e LENTO.
/// </summary>
internal sealed class FakeEngine : IScanEngine
{
    public bool Unavailable { get; init; }

    public int ScanCalls;

    public TaskCompletionSource SlowScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Name => "Motor falso (teste)";

    public Task<EngineInfo> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new EngineUnavailableException("motor de teste desligado");
        }

        return Task.FromResult(new EngineInfo(Name, "0.0", "0", null, "somente teste"));
    }

    public async Task<EngineVerdict> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ScanCalls);
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (text.Contains("LENTO", StringComparison.Ordinal))
        {
            SlowScanStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (text.Contains("ERRO-MOTOR", StringComparison.Ordinal))
        {
            throw new EngineException("falha simulada do motor");
        }

        if (text.Contains("MALWARE-FALSO", StringComparison.Ordinal))
        {
            return new EngineVerdict(VerdictKind.Detected, "Teste.MalwareFalso", null);
        }

        if (text.Contains("SUSPEITO-FALSO", StringComparison.Ordinal))
        {
            return new EngineVerdict(VerdictKind.Suspicious, "Heuristics.Teste", null);
        }

        if (text.Contains("LIMITE-FALSO", StringComparison.Ordinal))
        {
            return new EngineVerdict(VerdictKind.LimitsExceeded, "Heuristics.Limits.Exceeded.MaxFileSize", null);
        }

        return EngineVerdict.Clean;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class TestEnvironment
{
    public static string? ClamAvDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("COCYTUS_TEST_ENGINES");
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "clamd.exe")))
            {
                return dir;
            }

            // Procura engines\clamav subindo a partir da saída dos testes.
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "engines", "clamav");
                if (File.Exists(Path.Combine(candidate, "clamd.exe")))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            return null;
        }
    }

    public static string? OfficialDatabaseDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("COCYTUS_TEST_OFFICIAL_DB");
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                && Directory.EnumerateFiles(dir, "*.c?d").Any()
                ? dir
                : null;
        }
    }

    /// <summary>Cria uma junção (não exige administrador). Devolve false se o sistema recusar.</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using var process = Process.Start(info)!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}

/// <summary>Teste que precisa do ClamAV em engines\clamav (baixado por scripts\preparar-ambiente.cmd).</summary>
public sealed class ClamAvFactAttribute : FactAttribute
{
    public ClamAvFactAttribute()
    {
        if (TestEnvironment.ClamAvDir is null)
        {
            Skip = "ClamAV não encontrado (rode scripts\\preparar-ambiente.cmd ou defina COCYTUS_TEST_ENGINES).";
        }
    }
}

/// <summary>Teste que precisa do ClamAV e das bases oficiais (COCYTUS_TEST_OFFICIAL_DB).</summary>
public sealed class ClamAvOfficialFactAttribute : FactAttribute
{
    public ClamAvOfficialFactAttribute()
    {
        if (TestEnvironment.ClamAvDir is null || TestEnvironment.OfficialDatabaseDir is null)
        {
            Skip = "ClamAV ou bases oficiais não encontrados (defina COCYTUS_TEST_ENGINES e COCYTUS_TEST_OFFICIAL_DB).";
        }
    }
}
