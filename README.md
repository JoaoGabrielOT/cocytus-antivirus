# Cocytus

Antivírus livre e gratuito para Windows 11 x64. Interface em português do Brasil.
Verifica arquivos e pastas com o motor **ClamAV** — verificação **rápida**, **completa** ou **selecionada**, pelo
**botão direito do Explorer** ("Verificar com o Cocytus") e em **tempo real** nas pastas escolhidas —, mostra o resultado
separando ameaças, suspeitos, itens não analisados e erros de leitura, e permite mover detecções para uma **quarentena
reversível**. Tem também uma tela de **otimização** (uso do computador, limpeza de temporários, Lixeira, programas de
inicialização) e uma de **desinstalação de apps** com limpeza de restos.

> O tempo real do Cocytus verifica os arquivos **depois** de gravados; ele **não** bloqueia a abertura nem a execução
> de programas (isso exigiria um driver de kernel assinado pela Microsoft), não analisa comportamento e não consulta
> reputação em nuvem. "Nenhuma ameaça detectada" significa só que o ClamAV, com as bases instaladas, não reconheceu
> nada — não é garantia de que o arquivo seja seguro. O Cocytus funciona ao lado do Microsoft Defender e não o substitui.
> Não há equivalência de proteção com antivírus comerciais.

Código e releases: <https://github.com/JoaoGabrielOT/cocytus-antivirus>.

Licença: **GPL-2.0** (veja `LICENSE` e `THIRD-PARTY-NOTICES.md`). Nada depende de pagamento, conta ou chave.

## Situação dos recursos

| Recurso | Situação | Observação |
| --- | --- | --- |
| Verificação selecionada (arquivos, pastas, arrastar e soltar) | Implementado | Acompanhamento, cancelamento, limites de tamanho e tempo por arquivo |
| Verificação rápida | Implementado | Programas em execução, inicialização, Downloads, Área de Trabalho, Temp e AppData (só executáveis e scripts) |
| Verificação completa | Implementado | Todas as unidades fixas |
| Motor de detecção | Implementado | ClamAV 1.5.4 em processo separado; assinaturas oficiais e heurísticas do ClamAV |
| Arquivos compactados | Implementado | Abertos pelo ClamAV com limites de tamanho, profundidade, quantidade e tempo |
| Relatório por categoria | Implementado | Ameaça, suspeito, não analisado (com motivo), erro de leitura; mecanismo, versão da base e limitações |
| Quarentena reversível | Implementado | Confirmação, SHA-256, cópia cifrada, restauração sem sobrescrever, exclusão com dupla confirmação |
| Histórico local e configurações | Implementado | SQLite e JSON na pasta de dados |
| Atualização das bases | Implementado | Manual, pelo `freshclam` oficial (`database.clamav.net`) |
| Atualização do próprio Cocytus | Implementado | Releases do GitHub; consulta ao abrir e a cada 24 h, pergunta antes, confere SHA-256 e instala sozinho |
| "Verificar com o Cocytus" no botão direito | Implementado | Arquivos, pastas e unidades, com seleção múltipla; registro só em HKCU |
| Verificação em tempo real | Parcial | Verifica arquivos novos/alterados nas pastas vigiadas logo após a gravação; avisa e pede confirmação antes da quarentena. Não bloqueia |
| Bandeja e início com o Windows | Implementado | Ícone na bandeja; início automático opcional (HKCU `Run`), sem administrador |
| Detecção de PUA | Parcial | Opcional (desligada por padrão); depende das assinaturas PUA do ClamAV |
| Bloqueio antes da abertura/execução | Planejado | Exige minifiltro de kernel assinado pela Microsoft |
| Otimização | Implementado | Painel de uso, limpeza de temporários, Lixeira, ativar/desativar programas de inicialização |
| Desinstalação de apps | Implementado | Programas e apps da Microsoft Store; desinstalador oficial; restos com confirmação (pastas para a Lixeira, registro com cópia `.reg`) |
| Agendamento de verificações | Planejado | |
| YARA, verificação de assinatura digital, reputação em nuvem (opcional, com consentimento) | Planejado | |
| Remediação (registro, inicialização), autodefesa, drivers, firewall | Planejado | Fora do escopo do MVP |

## Requisitos

- Windows 11 x64 (testado no Windows 11 build 26200).
- Para compilar: nada instalado globalmente. O script baixa o SDK do .NET 10 para `.tools\dotnet`.
- Rede: na preparação (SDK, ClamAV, pacotes NuGet, bases), quando o usuário clica em **Atualizar bases** e na
  consulta de versão nova do Cocytus no GitHub (pode ser desligada).

## Instalar uma release

1. Baixe `Cocytus-X.Y.Z-win-x64.zip` em <https://github.com/JoaoGabrielOT/cocytus-antivirus/releases/latest>
   (opcional: confira o SHA-256 com o `.sha256` ao lado).
2. Extraia numa pasta sua (ex.: `%LOCALAPPDATA%\Programs\Cocytus`) e abra `Cocytus.exe`. Não precisa de .NET nem de
   administrador. Os binários não têm assinatura digital, então o SmartScreen pode avisar na primeira vez.
3. Na primeira vez, **Atualização → Atualizar bases**.

## Atualização automática

- O Cocytus consulta `https://api.github.com/repos/JoaoGabrielOT/cocytus-antivirus/releases/latest` ao abrir e a cada
  24 horas (Configurações → "Procurar versões novas do Cocytus"; botão manual na tela Atualização). Só é feita a
  requisição; nada do computador é enviado.
- Havendo versão maior, abre a tela **"Cocytus X disponível"** com as novidades e três opções: **Atualizar agora**,
  **Agora não** e **Pular esta versão**.
- **Atualizar agora**: baixa o `.zip` da release para `%LocalAppData%\Cocytus\atualizacoes`, confere o SHA-256 publicado no
  `.sha256` (diferente → cancela), extrai, inicia o Cocytus novo em modo instalação e fecha o atual. O instalador espera o
  Cocytus antigo fechar, copia os arquivos para a mesma pasta (guardando cópia dos substituídos em
  `atualizacoes\copia-anterior`; se a cópia falhar, tudo volta como estava) e abre o Cocytus atualizado, que avisa na
  bandeja. Bases, quarentena, histórico, configurações, menu do botão direito e início automático continuam valendo.
- Se a pasta do programa exigir administrador (ex.: Program Files), o Windows pede a confirmação só para a cópia.
- Log da instalação: `%LocalAppData%\Cocytus\logs\atualizacao.log`.

## Publicar uma versão nova

1. Atualize `<Version>` em `Directory.Build.props` (ex.: `0.3.0`) e faça commit.
2. Crie e envie a tag: `git tag v0.3.0` e `git push origin v0.3.0`.
3. O workflow `.github/workflows/release.yml` (Windows) baixa o SDK e o ClamAV oficial, compila, roda os testes, gera
   `Cocytus-0.3.0-win-x64.zip` + `.sha256` (`scripts\cocytus.ps1 -Task release -Version 0.3.0`) e cria a release
   com notas geradas dos commits. Os Cocytus instalados passam a oferecer a atualização.

Cada push na `main` roda o workflow `CI` (build, formatação e testes).

## Instalação do ambiente, build e execução

Os comandos abaixo são executados na raiz do projeto, em PowerShell (sem administrador). Cada `.cmd` em `scripts\`
faz o mesmo com dois cliques.

```powershell
# 1. Prepara o ambiente: SDK .NET 10 em .tools\dotnet, ClamAV oficial em engines\clamav,
#    bases do ClamAV para desenvolvimento em .dev\data\clamav-db   (scripts\preparar-ambiente.cmd)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cocytus.ps1 -Task prepare

# 2. Compila, confere formatação/estilo e roda os testes              (scripts\verificar.cmd)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cocytus.ps1 -Task verify

# 3. Gera o build autocontido em dist\Cocytus (inclui o motor)        (scripts\gerar-build.cmd)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cocytus.ps1 -Task publish

# 4a. Executa com os dados de desenvolvimento (.dev\data)              (scripts\abrir-cocytus-dev.cmd)
$env:COCYTUS_DATA = "$PWD\.dev\data"; .\dist\Cocytus\Cocytus.exe
# 4b. Ou executa normalmente (dados em %LocalAppData%\Cocytus)
.\dist\Cocytus\Cocytus.exe
```

Tarefas individuais: `-Task sdk`, `engines`, `db`, `build`, `format`, `test`, `publish`, `package`, `all`, `release`
(`-Version X.Y.Z` grava a versão nos assemblies; `package` gera o zip e o `.sha256` em `dist\release`).
Logs de cada execução ficam em `.dev\logs\` e o resumo em `.dev\logs\ultimo-status.txt`.

Linha de comando do `Cocytus.exe`: `--verificar <caminhos…>` (verifica ao abrir; usado pelo menu),
`--bandeja` (abre escondido na bandeja; usado ao iniciar com o Windows), `--dados <pasta>` (outra pasta de dados,
como `COCYTUS_DATA`). Se o Cocytus já estiver aberto, o pedido é repassado à instância aberta.

Na primeira execução normal (sem `COCYTUS_DATA`), abra **Atualização → Atualizar bases** antes de verificar.
O `dist\Cocytus` pode ser copiado para outra máquina Windows 11 x64: não precisa do .NET instalado.

### Onde o build grava fora do projeto

Nada de forma permanente. O SDK, os pacotes NuGet e os caches ficam em `.tools\` e `.cache\`. Ferramentas do .NET
e o PowerShell podem usar a pasta temporária do Windows (`%TEMP%`) durante a execução.

## Testes

`-Task test` roda 85 testes (xUnit), sem malware real:

- orquestrador com motor falso de teste: detecção, contagens, caminhos com espaços e acentos (inclusive CJK),
  arquivo acima do limite, arquivo bloqueado, alvo inexistente, junção não seguida, cancelamento, tempo esgotado,
  motor indisponível, erros do motor e limites;
- ClamAV real (se `engines\clamav` existir): assinatura de teste própria, conteúdo limpo e vazio, compactado acima do
  limite, varredura completa de pasta com nome acentuado, parar e reiniciar o motor, falta de bases;
- EICAR com as bases oficiais, montado **só em memória** e enviado ao `clamd` (nunca gravado em disco);
- quarentena com arquivos descartáveis: ida e volta sem perda, conteúdo alterado, conflito na restauração, pasta
  original removida, arquivo em uso, somente leitura, exclusão definitiva, cópia corrompida, identificador inválido;
- tempo real com motor falso e pastas descartáveis: arquivo novo (com e sem ameaça), renomeado, download parcial e
  pasta excluída ignorados, arquivo em uso analisado quando liberado, arquivo sem alteração não reanalisado, pasta
  movida para dentro da pasta vigiada, motor indisponível, pasta inexistente;
- linha de comando (`--verificar`, `--bandeja`, `--dados`, raiz de unidade entre aspas, caminho relativo);
- verificação rápida e completa: pasta restrita a executáveis, pasta completa dentro de restrita (analisada uma vez),
  pastas excluídas, montagem dos alvos, unidades fixas, coleta real dos processos em execução, interpretação de comandos
  de inicialização (aspas, espaços, rundll32, caminhos de serviço);
- otimização com pastas e chaves descartáveis (HKCU\Software\Cocytus.Tests, apagada no fim): limpeza só de arquivos
  antigos, sem seguir junções, sem tocar em somente leitura, filtro de nome, pastas-raiz recusadas; inicialização
  (estado do Gerenciador de Tarefas, desativar/reativar sem apagar o item); painel de uso; consulta à Lixeira;
- desinstalação com chaves descartáveis: regras de "Programas e Recursos", comando oficial e `msiexec /x`, restos só com
  nome exato, sem pastas protegidas, de editor inteiras ou com outro programa instalado; listagem de apps da Store;
- histórico, configurações e interpretação das respostas do `clamd`/`freshclam`.

`scripts\testar-integracao.cmd` lê do registro os comandos do menu e do início automático e executa o comando do menu
como o Explorer faria com 3 arquivos selecionados (descartáveis, em `.dev\teste-integracao`). Log em
`.dev\logs\integracao-*.log`.

`scripts\criar-app-de-teste.cmd` cria um "programa instalado" falso só no registro do usuário (com pasta em
`%LOCALAPPDATA%\Programs` e uma chave que sobram depois da desinstalação), para testar a tela Desinstalar apps.

Roteiro de teste manual da interface: `docs\ROTEIRO-TESTE-MANUAL.md`.

### Arquivo de teste

Qualquer arquivo que **comece** com `COCYTUS-ARQUIVO-DE-TESTE-INOFENSIVO` é detectado como
`Cocytus.ArquivoDeTeste.UNOFFICIAL` (assinatura própria, gravada pelo Cocytus na pasta de bases). A tela **Sobre**
cria esse arquivo. Ele não é malware e não aciona o Microsoft Defender.

## Comportamento definido

- **Links simbólicos e junções**: não são seguidos; aparecem como "não analisado" com o destino.
- **Arquivos só na nuvem** (OneDrive "somente online"): não são baixados; aparecem como "não analisado".
- **Arquivos grandes**: acima de 200 MB (configurável, 1–2000) não são enviados ao motor.
- **Compactados**: até 400 MB extraídos, 200 MB por arquivo interno, 16 níveis, 10 000 arquivos e 120 s por item
  (configurável). Excedido algum limite, o item aparece como "não analisado por completo".
- **Tempo por arquivo**: 180 s (configurável).
- **Erros** (acesso negado, arquivo em uso, removido durante a varredura): "erro de leitura"; a varredura continua.
- **Recursos**: no máximo 8 arquivos em paralelo (padrão 2), fila limitada, `clamd` com prioridade abaixo do normal.
  Com as bases completas o `clamd` usa cerca de 1 GB de RAM; dá para encerrá-lo após cada varredura nas configurações.

## Menu do botão direito, bandeja e início com o Windows

Em **Configurações → Integração com o Windows**:

- **"Verificar com o Cocytus"**: grava em `HKCU\Software\Classes\{*,Directory,Drive}\shell\Cocytus.Verificar`
  (só o usuário atual; sem administrador). No Windows 11 a opção aparece em **Mostrar mais opções** (menu clássico);
  o menu novo do Windows 11 só aceita extensões empacotadas (MSIX). Com vários itens selecionados, o Explorer abre um
  processo por item; o Cocytus junta os pedidos que chegam em sequência numa única verificação.
- **Iniciar com o Windows**: grava `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Cocytus` com `--bandeja`.
- **Remover / Desativar** apaga só essas chaves. Os comandos gravados apontam para o executável atual; se o
  `dist\Cocytus` mudar de lugar, adicione de novo.

Com o tempo real ligado, fechar a janela só a esconde na bandeja. Para encerrar: **Sair do Cocytus** (barra lateral)
ou **Sair** no menu do ícone da bandeja.

## Tempo real

Tela **Tempo real**. Pastas padrão: Downloads, Área de Trabalho, Documentos e a pasta temporária do usuário (a lista
pode ser trocada). Funcionamento:

- `FileSystemWatcher` em cada pasta (com subpastas). Cada arquivo criado, alterado ou renomeado entra numa fila e é
  analisado 1,5 s depois da última alteração; arquivo em uso é tentado de novo por até 2 min.
- Não analisa de novo um arquivo com mesmo tamanho e data já analisado; ignora downloads parciais
  (`.crdownload`, `.part`…), a pasta de dados e a pasta do próprio Cocytus.
- Detecção: aviso na bandeja, registro no histórico e linha na tela Tempo real. **Nada é movido sem confirmação.**
- Fila limitada a 5000 arquivos; acima disso, avisos novos são ignorados e contados. Se o Windows descartar avisos
  (muitas alterações de uma vez), o contador "estouros do buffer" sobe e a mensagem fica no log.
- O motor fica carregado enquanto o tempo real estiver ligado (cerca de 1 GB de RAM com as bases completas).

## Verificação rápida, completa e selecionada

- **Rápida**: o executável de cada programa em execução; os arquivos de cada item que inicia com o Windows (registro
  `Run`/`RunOnce` do usuário e de todos os usuários, pastas Inicializar, serviços com início automático; para
  `rundll32`/`regsvr32`/`wscript`, também o arquivo carregado); as pastas Downloads, Área de Trabalho e Temp inteiras;
  e, em AppData (Roaming, Local e LocalLow), só executáveis e scripts (`.exe`, `.dll`, `.ps1`, `.vbs`, `.js`, `.lnk`,
  `.msi`…). Uma pasta pedida inteira dentro de outra restrita (Temp dentro de Local) é verificada uma vez, inteira.
- **Completa**: todas as unidades fixas prontas (`C:\`, `D:\`…). Pede confirmação, pois pode levar horas.
  Pendrives, rede e arquivos só na nuvem ficam de fora; arquivos do sistema em uso aparecem como erro de leitura.
- **Selecionada**: os arquivos e pastas escolhidos (ou arrastados, ou vindos do menu do botão direito).
- Em todas, a pasta de dados e a pasta do programa do Cocytus não são percorridas. O histórico guarda o tipo e um
  resumo dos alvos.

## Otimização

- **Uso do computador**: espaço livre por unidade, memória em uso e os 10 programas que mais usam memória e CPU
  (processos agrupados pelo nome; CPU medida em 1 segundo). Só leitura.
- **Limpeza**: Temp do usuário (só arquivos sem alteração há mais de 24 h), cache de miniaturas (`thumbcache_*.db`) e
  relatórios de erro do Windows do usuário. "Analisar" mostra quantidade e tamanho; "Limpar…" pede confirmação e apaga
  definitivamente (são temporários). Não segue links nem junções e mantém arquivos em uso, somente leitura e de sistema.
- **Lixeira**: mostra itens e tamanho; "Esvaziar Lixeira…" pede confirmação.
- **Inicialização**: lista o que inicia com o Windows (registro `Run` do usuário e de todos os usuários, 64 e 32 bits, e
  pastas Inicializar). Desativar/ativar usa o mesmo ajuste do Gerenciador de Tarefas
  (`...\Explorer\StartupApproved`): o item não é apagado. Itens de todos os usuários são alterados pelo `reg.exe` com
  o aviso de administrador do Windows. Não há limpador de registro.

## Desinstalar apps

- Lista os programas com as mesmas regras de "Programas e Recursos" (registro `Uninstall` de HKLM 64/32 bits e de HKCU,
  sem componentes de sistema e atualizações) e os apps da Microsoft Store do usuário (API `PackageManager` do Windows,
  sem frameworks nem componentes do sistema). Busca por nome ou editor.
- **Programa**: abre o desinstalador oficial (`UninstallString`; para Windows Installer, `msiexec /x {código}`), que pede
  administrador se precisar. O Cocytus espera a entrada sumir do registro (até 2 minutos depois que o desinstalador
  fecha; "Parar de aguardar" interrompe). Se o programa continuar instalado, nada é removido.
- **Restos**: só depois de desinstalado. Procura a pasta de instalação registrada e pastas e chaves com o **nome exato**
  do programa (ou Editor\Programa) em Program Files, ProgramData, AppData (Roaming, Local, Local\Programs, LocalLow) e
  `Software` de HKCU/HKLM. Nunca sugere pastas do sistema ou do perfil, pastas de editores inteiras, pastas com nomes
  genéricos nem pastas que contenham outro programa instalado. A lista vem marcada; o usuário desmarca o que quiser
  manter e confirma. Pastas vão para a **Lixeira**; chaves são exportadas para `.reg` em
  `%LocalAppData%\Cocytus\backups\registro` e depois apagadas (HKLM pelo `reg.exe` com aviso de administrador).
- **App da Store**: removido pela API do Windows, com confirmação.

## Quarentena

Antes de mover, o Cocytus pede confirmação, abre o arquivo impedindo que outros programas o alterem, confere o SHA-256
com o da detecção, grava uma cópia cifrada (AES-256, chave por item), confere a cópia e só então remove o original.
Registra caminho original, SHA-256, motivo, motor/base e data. A restauração pede confirmação, confere o SHA-256 e
nunca sobrescreve: se o destino existir, oferece outro nome. A chave fica junto do registro, então a cifragem evita
execução acidental, mas não protege contra quem tem acesso à pasta de dados.

## Estrutura

```
src/Cocytus.Core      modelos, contrato do motor, orquestrador de varredura, tempo real, linha de comando
src/Cocytus.Engines   ClamAV (clamd + INSTREAM), leitura das bases
src/Cocytus.Data      quarentena, histórico (SQLite), configurações
src/Cocytus.Update    freshclam
src/Cocytus.Maintenance  locais das verificações rápida/completa, uso do sistema, limpeza, inicialização, apps instalados e restos
src/Cocytus.App       interface WPF, bandeja, instância única, integração com o Windows (HKCU)
tests/Cocytus.Tests   testes xUnit
scripts/              preparar ambiente, verificar, gerar build, abrir com dados de dev, testar integração, criar app de teste, gerar ícone
docs/                 arquitetura, relação com o material de referência, roteiro de teste manual
```

## Limitações conhecidas

- O tempo real verifica depois da gravação: um programa pode ser aberto antes da análise terminar. Sem bloqueio,
  sem remediação de sistema, sem verificação de memória/processos.
- O tempo real só vê o que acontece nas pastas vigiadas e depende dos avisos do Windows; em rajadas muito grandes
  parte dos avisos pode ser perdida (o contador mostra quando isso acontece).
- No Windows 11 o menu do botão direito fica em "Mostrar mais opções".
- A verificação rápida não lê tarefas agendadas, extensões de navegador nem serviços hospedados no `svchost`; a completa
  não entra em pendrives, unidades de rede nem arquivos só na nuvem.
- Desinstalação: depende do desinstalador de cada programa; desinstaladores que só terminam depois de reiniciar o
  Windows deixam o programa "ainda instalado" e os restos não são procurados. A busca de restos usa só nomes exatos e
  pode não encontrar tudo o que um programa deixou.
- Os executáveis das releases não têm assinatura Authenticode (certificado de assinatura é pago); a integridade da
  atualização automática é conferida pelo SHA-256 publicado na mesma release, o que protege contra download corrompido,
  mas não contra quem controlar a conta do GitHub do projeto.
- A API de apps da Store exige o Windows 10 2004 ou mais novo; o pacote inclui as projeções do Windows SDK
  (`Microsoft.Windows.SDK.NET.dll`, cerca de 25 MB).
- Detecção limitada às assinaturas e heurísticas do ClamAV; a taxa de detecção do ClamAV para ameaças novas de Windows
  é inferior à de produtos comerciais.
- O `clamd` aceita conexões de qualquer processo local na porta aleatória enquanto estiver carregado.
- Os binários oficiais do ClamAV para Windows não têm assinatura Authenticode; a integridade foi conferida pelo SHA-256
  publicado no GitHub.
- O Cocytus não se registra na Central de Segurança do Windows (isso exige parceria com a Microsoft).
