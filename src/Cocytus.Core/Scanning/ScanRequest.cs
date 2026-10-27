namespace Cocytus.Core.Scanning;

public enum ScanKind
{
    /// <summary>Arquivos e pastas escolhidos pelo usuário (inclui o menu do botão direito).</summary>
    Selected,

    /// <summary>Locais de risco: processos em execução, itens de inicialização, pastas do usuário e AppData.</summary>
    Quick,

    /// <summary>Todas as unidades fixas.</summary>
    Full,
}

/// <summary>Um alvo da varredura. Com <see cref="ExecutablesOnly"/>, só arquivos executáveis e scripts da pasta são analisados.</summary>
public sealed record ScanTarget(string Path, bool ExecutablesOnly = false);

/// <summary>Pedido de varredura.</summary>
/// <param name="Kind">Tipo da varredura.</param>
/// <param name="Targets">Alvos (arquivos ou pastas).</param>
/// <param name="ExcludedFolders">Pastas nunca percorridas (dados e programa do Cocytus).</param>
/// <param name="Description">O que aparece no histórico como "alvos"; se nulo, a lista de caminhos.</param>
public sealed record ScanRequest(
    ScanKind Kind,
    IReadOnlyList<ScanTarget> Targets,
    IReadOnlyList<string> ExcludedFolders,
    IReadOnlyList<string>? Description = null)
{
    public static ScanRequest ForSelected(IEnumerable<string> paths) =>
        new(ScanKind.Selected, paths.Select(p => new ScanTarget(p)).ToList(), []);
}

/// <summary>Extensões tratadas como executáveis ou scripts (usadas onde a varredura é restrita, como o AppData).</summary>
public static class ExecutableTypes
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".scr", ".com", ".pif", ".cpl", ".ocx", ".drv", ".efi",
        ".msi", ".msp", ".msix", ".appx", ".appxbundle", ".msixbundle",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".sct",
        ".lnk", ".url", ".jar", ".reg", ".inf", ".chm", ".xll", ".application",
    };

    public static IReadOnlyCollection<string> All => Extensions;

    public static bool Matches(string path) => Extensions.Contains(Path.GetExtension(path));
}
