# Cocytus — instruções para o agente

Antivírus (sob demanda, botão direito do Explorer e tempo real pós-gravação) para Windows 11 (C# / .NET 10 / WPF) com motor ClamAV em processo separado. Licença GPL-2.0.

## Comandos (PowerShell, na raiz do projeto)

- Preparar ambiente (SDK local, ClamAV oficial, bases de dev): `scripts\cocytus.ps1 -Task prepare`
- Compilar + formatação + testes: `scripts\cocytus.ps1 -Task verify`
- Só compilar / testar / formatação: `-Task build` / `-Task test` / `-Task format`
- Build distribuível (`dist\Cocytus`): `scripts\cocytus.ps1 -Task publish`
- Abrir o build com dados de dev: `scripts\abrir-cocytus-dev.cmd`
- Testar menu do botão direito / início automático já registrados (HKCU): `scripts\testar-integracao.cmd`
- Criar um programa instalado falso (só HKCU) para testar a desinstalação: `scripts\criar-app-de-teste.cmd`
- Logs de cada execução: `.dev\logs\`; resumo: `.dev\logs\ultimo-status.txt`

O script usa só o SDK em `.tools\dotnet` e caches em `.cache\` (nada global).

## Estrutura

- `src/Cocytus.Core` — modelos, contrato `IScanEngine`, orquestrador de varredura, `RealTimeMonitor`, `CommandLineOptions` (sem UI).
- `src/Cocytus.Engines` — ClamAV: supervisão do `clamd`, protocolo INSTREAM, leitura das bases.
- `src/Cocytus.Data` — quarentena, histórico (SQLite), configurações (JSON).
- `src/Cocytus.Update` — atualização das bases pelo `freshclam`; releases do GitHub (`GitHubReleaseClient`) e
  instalação da versão nova (`UpdateInstaller`, usado pelo `Cocytus.exe --aplicar-atualizacao`).
- `src/Cocytus.Maintenance` — locais das verificações rápida/completa, uso do sistema, limpeza, Lixeira, inicialização
  (StartupApproved), apps instalados (registro Uninstall + PackageManager da Store), busca e remoção de restos.
- `src/Cocytus.App` — interface WPF (MVVM sem framework externo), bandeja (NotifyIcon), instância única (mutex + pipe), `WindowsIntegration` (HKCU).
- `tests/Cocytus.Tests` — xUnit; testes com ClamAV real usam `[ClamAvFact]`.

## Convenções

- Textos de usuário em português do Brasil. Código e nomes de tipos em inglês.
- `TreatWarningsAsErrors` ligado; `dotnet format --verify-no-changes` precisa passar.
- Operações demoradas fora da thread da UI (`Task.Run`), sempre com `CancellationToken`.
- Falhas de leitura/análise nunca contam como "sem detecção".

## Limites de atuação

- `D:\.Minhas_Coisas\Engenharia Reversa` é somente leitura e só serve de referência conceitual.
  Não copiar código descompilado, marcas, textos, ícones nem bases do produto analisado.
- Não alterar Defender, serviços, firewall ou drivers. O Cocytus não roda elevado; ações em HKLM/Program Files usam
  `reg.exe` com `runas` ou a operação de arquivo do shell, sempre com confirmação do usuário. Registro: só as chaves HKCU do menu
  (`Software\Classes\{*,Directory,Drive}\shell\Cocytus.Verificar`) e do início automático (`...\Run\Cocytus`),
  e só por ação do usuário na tela Configurações.
- Testes: só arquivos descartáveis e a assinatura de teste própria; EICAR só em memória. Nada de malware real.
  Chaves de registro de teste só em `HKCU\Software\Cocytus.Tests` (apagadas no fim). Não rodar limpeza, esvaziar
  Lixeira, desinstalar nem verificações rápida/completa no computador do usuário durante o desenvolvimento.
- Rede: download do SDK/ClamAV/NuGet (scripts), das bases do ClamAV (freshclam) e consulta/download das releases do
  próprio Cocytus no GitHub (`JoaoGabrielOT/cocytus-antivirus`). Nenhuma telemetria.
- Releases: mudar `<Version>` no `Directory.Build.props` e dar push na `main` → workflow `release.yml` cria a tag
  `vX.Y.Z` e a release (`scripts\cocytus.ps1 -Task release -Version X.Y.Z`). O pacote precisa
  se chamar `Cocytus-X.Y.Z-win-x64.zip` e ter o `.sha256` ao lado; é isso que o atualizador procura.
- Não apagar arquivos do usuário nem fazer commit/push sem autorização.
