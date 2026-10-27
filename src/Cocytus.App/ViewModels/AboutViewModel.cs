using System.Reflection;
using System.Windows.Input;
using Cocytus.App.Services;
using Cocytus.Engines.ClamAv;

namespace Cocytus.App.ViewModels;

public sealed record FeatureStatus(string Feature, string Status, string Note);

public sealed class AboutViewModel : PageViewModel
{
    private readonly AppServices _services;

    public AboutViewModel(AppServices services)
    {
        _services = services;
        CreateTestFileCommand = new RelayCommand(CreateTestFile);
    }

    public override string Title => "Sobre";

    public override string Glyph => "";

    public ICommand CreateTestFileCommand { get; }

    public string VersionText =>
        "Versão " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");

    public string EngineText => _services.EngineVersionText;

    public string TestMarker => ClamAvDatabase.TestMarker;

    public IReadOnlyList<FeatureStatus> Features { get; } =
    [
        new("Verificação sob demanda de arquivos e pastas", "Implementado", "Com acompanhamento, cancelamento e limites de tamanho e tempo."),
        new("\"Verificar com o Cocytus\" no botão direito", "Implementado", "Arquivos, pastas e unidades; ativado nas Configurações (HKCU). No Windows 11, em \"Mostrar mais opções\"."),
        new("Verificação em tempo real", "Parcial", "Verifica arquivos novos ou alterados nas pastas vigiadas logo após a gravação; avisa e pede confirmação. Não bloqueia abertura nem execução."),
        new("Motor de detecção", "Implementado", "ClamAV (assinaturas oficiais e heurísticas do ClamAV), em processo separado."),
        new("Arquivos compactados", "Implementado", "Abertos pelo ClamAV, com limites de tamanho, profundidade, quantidade e tempo."),
        new("Quarentena reversível", "Implementado", "Com confirmação, conferência por SHA-256 e restauração sem sobrescrever."),
        new("Histórico e configurações", "Implementado", "Locais (SQLite e JSON)."),
        new("Atualização das bases", "Implementado", "Manual, pelo freshclam oficial."),
        new("Bandeja e início com o Windows", "Implementado", "Início automático opcional (HKCU\\...\\Run), sem administrador."),
        new("Detecção de PUA", "Parcial", "Opcional, desligada por padrão; depende das assinaturas PUA do ClamAV."),
        new("Bloqueio antes da abertura/execução", "Planejado", "Exige minifiltro de kernel assinado pela Microsoft."),
        new("Verificação rápida, completa e selecionada", "Implementado", "Rápida: programas em execução, inicialização, pastas do usuário e AppData. Completa: todas as unidades fixas."),
        new("Agendamento de verificações", "Planejado", "Hoje as verificações são iniciadas pelo usuário."),
        new("Otimização", "Implementado", "Painel de uso (discos, memória, programas), limpeza de temporários, Lixeira e programas de inicialização."),
        new("Desinstalação de apps", "Implementado", "Programas e apps da Microsoft Store; desinstalador oficial e limpeza de restos com confirmação (pastas para a Lixeira, registro com cópia .reg)."),
        new("Regras YARA, assinatura digital, reputação em nuvem", "Planejado", "Não implementados."),
        new("Remediação de sistema, autodefesa, firewall", "Planejado", "Fora do escopo desta versão."),
    ];

    public string Limitations =>
        "O Cocytus verifica sob demanda e, se ativado, em tempo real nas pastas escolhidas, logo depois que os arquivos são gravados. "
        + "Ele não bloqueia a abertura nem a execução de programas, não analisa comportamento nem consulta reputação em nuvem. A detecção depende do ClamAV e das bases instaladas; \"nenhuma ameaça "
        + "detectada\" não é garantia de que um arquivo seja seguro. O Cocytus não substitui o Microsoft Defender e funciona ao "
        + "lado dele. Não há equivalência de proteção com antivírus comerciais.";

    private void CreateTestFile()
    {
        var path = Dialogs.PickSavePath("Salvar arquivo de teste do Cocytus",
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "arquivo-de-teste-cocytus.txt");
        if (path is null)
        {
            return;
        }

        if (File.Exists(path))
        {
            Dialogs.Error("Já existe um arquivo com esse nome. Escolha outro nome.");
            return;
        }

        File.WriteAllText(path, TestMarker + Environment.NewLine
            + "Arquivo de teste inofensivo do Cocytus. Ele é detectado de propósito pela assinatura \"Cocytus.ArquivoDeTeste\" "
            + "para você testar a verificação e a quarentena sem usar malware." + Environment.NewLine);
        _services.Log.Info($"Arquivo de teste criado: {path}");
        Dialogs.Info($"Arquivo de teste criado em:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}Verifique-o na tela Verificar.");
    }
}
