# Arquitetura do Cocytus e relação com o material de referência

## Visão geral

```
┌────────────────────── Cocytus.exe (usuário comum, sem elevação) ───────────────────────┐
│ Cocytus.App (WPF, MVVM)                                                                  │
│   Verificar · Tempo real · Quarentena · Histórico · Atualização · Configurações · Sobre  │
│   Bandeja (NotifyIcon) · instância única (mutex + pipe) · integração HKCU (menu, Run)    │
│        │ Task.Run / Progress<T> / CancellationToken (nada pesado na thread da UI)         │
│ Cocytus.Core ── ScanOrchestrator ── FileEnumerator (não segue links, não baixa da nuvem) │
│        │      RealTimeMonitor (FileSystemWatcher → fila → FileScanner) ──┐               │
│        │            │ fila limitada (Channel 256) + N trabalhadores + limite de tamanho   │
│        │            │ e de tempo por arquivo                                              │
│        │            ▼                                                                     │
│        │      IScanEngine ◄── Cocytus.Engines.ClamAvEngine ──TCP 127.0.0.1 (INSTREAM)──┐ │
│ Cocytus.Data: QuarantineStore (AES-256 + SHA-256) · HistoryStore (SQLite) · Settings    │ │
│ Cocytus.Update: FreshclamUpdater ── freshclam.exe ── database.clamav.net                │ │
│ Cocytus.Maintenance: ScanLocations · SystemUsage · TempCleaner · StartupManager ·        │ │
│                      InstalledApps/StoreApps · LeftoverFinder/Remover                    │ │
└─────────────────────────────────────────────────────────────────────────────────────────┼─┘
                                     clamd.exe (processo filho, Job Object, prioridade ◄─┘
                                     abaixo do normal, porta livre escolhida a cada início)
```

- A interface só apresenta e coleta; a decisão sobre cada arquivo vem do motor; o orquestrador classifica.
- O conteúdo é lido pelo Cocytus (compartilhamento de leitura/escrita/exclusão, sem bloquear outros programas)
  e enviado ao `clamd`. O arquivo nunca é aberto pelo shell nem executado.
- O `clamd` é filho de um Job Object com `KILL_ON_JOB_CLOSE`: se o Cocytus fechar ou cair, o Windows encerra o motor.

## Verificação rápida, completa e selecionada

- `ScanRequest` (Core) descreve a varredura: tipo, alvos (`ScanTarget`, com `ExecutablesOnly` para pastas restritas a
  executáveis e scripts), pastas excluídas e o texto que o histórico mostra.
- `FileEnumerator.NormalizeTargets` junta repetidos e remove alvos cobertos por outra pasta; uma pasta completa dentro
  de uma restrita continua como alvo próprio e a restrita não entra nela (cada arquivo é analisado uma vez).
- `ScanLocations` (Maintenance) monta a rápida a partir de: imagens dos processos (`QueryFullProcessImageName` com
  `PROCESS_QUERY_LIMITED_INFORMATION`, sem elevação), comandos de inicialização (`Run`/`RunOnce`, pastas Inicializar,
  `ImagePath` de serviços automáticos), Downloads/Área de Trabalho/Temp e AppData restrito. A completa usa as unidades
  fixas prontas. Pasta de dados e pasta do programa são sempre excluídas.

## Otimização e desinstalação

- Tudo em modo usuário. Leitura: `DriveInfo`, `GlobalMemoryStatusEx`, tempos de CPU dos processos, `SHQueryRecycleBin`,
  registro `Run`/`StartupApproved`/`Uninstall`, `PackageManager.FindPackagesForUser("")`.
- Escrita só com confirmação: `TempCleaner` (exclusão de temporários, sem seguir pontos de nova análise),
  `SHEmptyRecycleBin`, valor de 12 bytes em `StartupApproved` (o mesmo formato do Gerenciador de Tarefas), desinstalador
  oficial via `ShellExecute` (eleva sozinho se o manifesto pedir), `PackageManager.RemovePackageAsync`.
- HKLM e Program Files: `reg.exe` com verbo `runas` (aviso do UAC) e operação de arquivo do shell (envia para a Lixeira,
  com o pedido de permissão do próprio Windows). Chaves removidas são exportadas antes para `.reg`.
- `LeftoverFinder` só sugere nomes exatos, dentro das pastas de software conhecidas, e recusa pastas protegidas, pastas
  de editores e pastas que contenham outro programa ainda instalado.

## Atualização do próprio Cocytus

- `GitHubReleaseClient` (Update) lê `releases/latest` do repositório público, ignora rascunhos e pré-releases e só aceita a
  release que tem `Cocytus-X.Y.Z-win-x64.zip` e `Cocytus-X.Y.Z-win-x64.zip.sha256`. Compara com a versão do assembly.
- `AppUpdateService` (App) agenda a consulta (20 s depois de abrir e de hora em hora, consultando de fato a cada 24 h),
  respeita a versão pulada e a opção das Configurações, e abre a `AppUpdateWindow`.
- Instalação em dois processos: o Cocytus atual baixa, confere o SHA-256 e extrai; inicia o `Cocytus.exe` da versão nova com
  `--aplicar-atualizacao <pasta do programa> --pid <pid>` (com `runas` se a pasta não aceitar escrita) e fecha.
  O processo novo (`UpdateApplier`) espera o antigo terminar, chama `UpdateInstaller.Apply` (cópia com backup e restauração
  em caso de erro) e abre o Cocytus da pasta do programa com `--atualizado` (pelo Explorador, se estiver elevado, para não
  herdar o administrador).
- Releases: versão nova no `Directory.Build.props` na `main` → `.github/workflows/release.yml` em `windows-latest` →
  `cocytus.ps1 -Task release` → tag `vX.Y.Z` e release com o zip e o `.sha256`.

## Tempo real

- `RealTimeMonitor` (Core) não conhece WPF: recebe as pastas, as exclusões e uma função que entrega o motor.
  Os `FileSystemWatcher` só colocam o caminho numa fila (`ConcurrentDictionary`, no máximo 5000); um laço analisa cada
  caminho 1,5 s depois da última alteração, com o mesmo `FileScanner` da verificação sob demanda (um arquivo por vez).
- Arquivo em uso: nova tentativa até 2 min. Arquivo já analisado com mesmo tamanho e data: ignorado (até 20 000
  lembrados). Pasta nova movida para dentro: enumerada (até 2000 arquivos).
- Detecção vira evento: a App registra no histórico, mostra aviso na bandeja e uma linha na tela Tempo real.
  A quarentena só acontece com confirmação, pelo mesmo `QuarantineStore`.
- Motor indisponível: estado "Sem motor", nova tentativa a cada 30 s. Avisos perdidos pelo Windows (estouro do buffer)
  e por fila cheia são contados separadamente e registrados no log.
- Não há bloqueio: isso exigiria um minifiltro de kernel (como o do produto de referência), assinado pela Microsoft.

## Botão direito e instância única

- O verbo `Cocytus.Verificar` em `HKCU\Software\Classes\{*,Directory,Drive}\shell` executa
  `"Cocytus.exe" --verificar "%1"` (com `--dados` se a pasta de dados não for a padrão). `MultiSelectModel=Player`
  permite seleção múltipla; o Explorer abre um processo por item.
- O primeiro processo cria o mutex `Local\Cocytus.Instancia.v1` e um pipe nomeado restrito ao usuário atual
  (`Cocytus.Instancia.<SID>.<sessão>`). Os seguintes enviam os caminhos (já convertidos em absolutos) por esse pipe e
  encerram. A janela junta os pedidos que chegam em até 700 ms numa única verificação.

## Regras de classificação

| Situação | Categoria | Motivo exibido |
| --- | --- | --- |
| `stream: OK` | Nenhuma ameaça detectada | — |
| `... FOUND` (assinatura comum) | Ameaça detectada | nome da assinatura |
| `Heuristics.*` (exceto limites) ou `PUA.*` | Suspeito | nome da assinatura |
| `Heuristics.Limits.Exceeded.*` | Não analisado | limites de compactado excedidos |
| Arquivo maior que o limite configurado | Não analisado | acima do limite de tamanho |
| Tempo por arquivo esgotado | Não analisado | tempo esgotado |
| Link simbólico / junção | Não analisado | não seguido (destino informado) |
| Arquivo "somente online" (OneDrive) | Não analisado | não baixado |
| `... ERROR` do clamd / falha de comunicação | Não analisado | erro do motor |
| Acesso negado, arquivo bloqueado, removido | Erro de leitura | descrição do erro |

Itens não analisados e erros de leitura não entram na contagem de "analisados" nem de "sem detecção".

## Material de referência: o que foi usado e como

A pasta `D:\.Minhas_Coisas\Engenharia Reversa\saidas` foi lida apenas como referência conceitual. Nenhum código
descompilado, texto, marca, ícone ou base foi copiado.

| Conceito observado no material | Arquivo de evidência | Tipo de evidência | No Cocytus |
| --- | --- | --- | --- |
| UI separada do núcleo; a UI só mostra e coleta | `05-dotnet-ui/dotnet-ui.analise.md`, DOC2 §0 e §D | Metadados .NET (demonstrado) | Projetos separados: App × Core/Engines/Data/Update |
| Tarefas de varredura (objetos, rápida, completa…) | `04-nucleo/nucleo.decompiled.c` (cabeçalho), DOC2 §A.1 | Strings (demonstrado); comportamento de cada tarefa desconhecido | Só "objetos" (arquivos/pastas escolhidos) |
| Veredito com status, nome, tipo e revisão das bases | DOC2 §A.4 | Strings (demonstrado); lógica desconhecida | `FileScanResult` + `EngineInfo` com versão e data da base |
| Motor de assinaturas desacoplado e carregado como serviço | DOC2 §A.2, `nucleo.decompiled.c` | Strings/pseudocódigo (demonstrado); motor real não analisado | `IScanEngine` + ClamAV em processo separado |
| Desempacotamento de formatos | `01-arquitetura/inventario-modulos.md` §4 | Lista de DLLs (demonstrado) | Feito pelo ClamAV, com limites explícitos |
| Atualização das bases | DOC2 §A.2 | Strings (demonstrado) | freshclam manual + recarga do motor |
| Consentimento antes de ações de remediação | `02-avz-engine/avzscan.decompiled.c` (FUN_1000dc19) | Pseudocódigo (demonstrado) | Confirmação antes de mover para a quarentena, restaurar ou excluir |
| Backup antes de alterar | DOC2 §E.5, `file_backup_manager` | Inferido | Quarentena reversível conferida por SHA-256 |
| Confiança local antes de consultar nuvem | `avzscan.decompiled.c` (FUN_1000c8d9) | Pseudocódigo parcial (ramo do KSN incompleto) | Não implementado (exigiria serviço externo e envio de dados) |
| Remediação por scripts, interceptação em kernel, autodefesa, firewall, controle de dispositivos | DOC2 §B e §C, `03-autodefesa/system_interceptors.analise.md` | Strings e caminhos de código-fonte; drivers não descompilados | Fora do escopo desta versão |
| Licenciamento/ativação | `dotnet-ui.analise.md` §4–5 | Metadados | Não se aplica: o Cocytus é gratuito |

Desconhecido a partir do material: lógica real de detecção e heurística do produto de referência, formato das bases,
regras de decisão em execução, conteúdo dos scripts de remediação e comportamento dos drivers.

## Decisões de segurança

- Sem elevação (`asInvoker`); nada em serviços, firewall ou Defender. Registro: chaves HKCU do menu e do início
  automático (Configurações), `StartupApproved` (Otimização) e remoção de restos de programas desinstalados
  (Desinstalar apps), sempre por ação do usuário com confirmação; em HKLM, pelo `reg.exe` elevado pelo UAC.
- `clamd` só em 127.0.0.1, porta aleatória. Limitação: outro processo local do mesmo computador pode se conectar a
  essa porta enquanto o motor estiver carregado.
- Quarentena: handle com `FILE_SHARE_READ` (impede escrita/exclusão por outros), SHA-256 conferido com o da detecção,
  cópia cifrada verificada antes de apagar o original pelo mesmo handle; nenhuma exclusão definitiva sem dupla confirmação.
- Restauração nunca sobrescreve: grava em arquivo temporário, confere o SHA-256 e só então renomeia.
- A chave AES de cada item fica no registro JSON ao lado da cópia: a cifragem impede execução acidental e remoção da cópia
  por outros antivírus, mas não é proteção contra quem tem acesso à pasta de dados.
