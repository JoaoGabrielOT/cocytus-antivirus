namespace Cocytus.Core;

/// <summary>
/// Pastas usadas pelo Cocytus. Por padrão os dados ficam em %LocalAppData%\Cocytus;
/// a variável de ambiente COCYTUS_DATA troca a raiz (usada no desenvolvimento e nos testes).
/// </summary>
public sealed class CocytusPaths
{
    public const string DataEnvironmentVariable = "COCYTUS_DATA";
    public const string EnginesEnvironmentVariable = "COCYTUS_ENGINES";

    public CocytusPaths(string dataRoot, string clamAvDir)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        ClamAvDir = Path.GetFullPath(clamAvDir);
    }

    public string DataRoot { get; }

    public string ClamAvDir { get; }

    public string ClamAvDatabaseDir => Path.Combine(DataRoot, "clamav-db");

    public string QuarantineDir => Path.Combine(DataRoot, "quarentena");

    public string LogsDir => Path.Combine(DataRoot, "logs");

    public string ConfigDir => Path.Combine(DataRoot, "config");

    public string TempDir => Path.Combine(DataRoot, "tmp");

    public string HistoryDatabase => Path.Combine(DataRoot, "historico.db");

    public string SettingsFile => Path.Combine(DataRoot, "configuracoes.json");

    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cocytus");

    /// <summary>True quando a pasta de dados não é a padrão (precisa ir junto na linha de comando registrada no Windows).</summary>
    public bool UsesCustomDataRoot =>
        !string.Equals(Path.TrimEndingDirectorySeparator(DataRoot), Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultDataRoot)), StringComparison.OrdinalIgnoreCase);

    /// <param name="dataOverride">Pasta de dados vinda de --dados; tem prioridade sobre COCYTUS_DATA.</param>
    public static CocytusPaths FromEnvironment(string? dataOverride = null)
    {
        var data = string.IsNullOrWhiteSpace(dataOverride) ? Environment.GetEnvironmentVariable(DataEnvironmentVariable) : dataOverride;
        if (string.IsNullOrWhiteSpace(data))
        {
            data = DefaultDataRoot;
        }

        var engines = Environment.GetEnvironmentVariable(EnginesEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(engines))
        {
            engines = Path.Combine(AppContext.BaseDirectory, "engines", "clamav");
        }

        return new CocytusPaths(data, engines);
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataRoot, ClamAvDatabaseDir, QuarantineDir, LogsDir, ConfigDir, TempDir })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
