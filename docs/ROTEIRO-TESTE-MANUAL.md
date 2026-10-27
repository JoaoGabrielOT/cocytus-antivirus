# Roteiro de teste manual da interface

Pré-requisitos: `scripts\preparar-ambiente.cmd` e `scripts\gerar-build.cmd` executados. Use
`scripts\abrir-cocytus-dev.cmd` para abrir o build com os dados de desenvolvimento (`.dev\data`), ou
`dist\Cocytus\Cocytus.exe` para usar `%LocalAppData%\Cocytus`.

Use só arquivos descartáveis. Para detecção, use o arquivo de teste do Cocytus (não é malware).

| # | Passo | Resultado esperado |
| --- | --- | --- |
| 1 | Abrir o Cocytus | Janela "Cocytus" com ícone de cristal; rodapé mostra a versão da base daily e "Tempo real ativo · verifica após a gravação, não bloqueia" (ou "Tempo real iniciando…" enquanto o motor carrega) |
| 2 | Sobre → Criar arquivo de teste… → salvar numa pasta descartável com espaço e acento no nome (ex.: `D:\teste cocytus\ação`) | Mensagem de arquivo criado |
| 3 | Verificar → Adicionar pasta… → escolher a pasta do passo 2 (ou arrastá-la para a lista) | A pasta aparece na lista de alvos |
| 4 | Iniciar verificação | Texto "Carregando o motor…" na primeira vez; depois contadores sobem; a janela continua respondendo (mover, trocar de tela) |
| 5 | Ao terminar | "1 ameaça(s) detectada(s)"; linha "Ameaça detectada" com `Cocytus.ArquivoDeTeste.UNOFFICIAL`; resumo mostra mecanismo, versão da base, limitações e o aviso de que "nenhuma ameaça" não é garantia |
| 6 | Selecionar a linha → Mostrar no Explorador | Explorador abre com o arquivo selecionado (sem abri-lo) |
| 7 | Mover para a quarentena… → Não | Nada muda; arquivo continua no lugar |
| 8 | Mover para a quarentena… → Sim | Mensagem "1 de 1 arquivo(s) movido(s)"; arquivo some da pasta; coluna Situação = "Em quarentena" |
| 9 | Quarentena | Item listado com data, caminho original, motivo, motor/base, tamanho e SHA-256 |
| 10 | Criar manualmente um arquivo com o mesmo nome no local original → Restaurar… → Sim | Aviso de que já existe um arquivo; oferece escolher outro nome. Escolher "(restaurado)" → arquivo restaurado com outro nome; o arquivo criado manualmente fica intacto |
| 11 | Repetir 3–8 e depois Quarentena → Excluir definitivamente… | Duas confirmações; item some da lista |
| 12 | Verificar uma pasta com muitos arquivos (ex.: `.tools\dotnet` do próprio projeto) e clicar Cancelar logo depois | Texto "Cancelando…"; termina como "Varredura cancelada (resultado parcial)"; contadores param |
| 13 | Verificar um arquivo que outro programa mantém aberto com bloqueio exclusivo | Linha "Erro de leitura" com "Arquivo em uso por outro processo"; a varredura continua |
| 14 | Configurações → Tamanho máximo = 1 → Salvar → verificar um arquivo maior que 1 MB | Linha "Não analisado · Acima do limite de tamanho" |
| 15 | Configurações → Restaurar padrões → Salvar | Valores voltam a 200 MB, 2, 180 s… |
| 16 | Atualização → Atualizar bases | Saída do freshclam aparece; status "Bases atualizadas…" ou "As bases já estavam atualizadas (conferido agora no servidor)."; tabela de bases preenchida |
| 17 | Histórico | Varreduras listadas com contagens; ao selecionar, aparecem os itens; aba Eventos mostra quarentena, restauração, exclusão e atualização |
| 18 | Sair do Cocytus (barra lateral) | Janela e ícone da bandeja somem; processo `clamd.exe` some do Gerenciador de Tarefas |

## Tempo real, bandeja e menu do botão direito

| # | Passo | Resultado esperado |
| --- | --- | --- |
| 19 | Abrir o Cocytus → Tempo real | "Situação: Ativo" depois que o motor carrega; pastas padrão listadas (Downloads, Área de Trabalho, Documentos, Temp) |
| 20 | Adicionar pasta… → uma pasta descartável (ex.: `D:\teste cocytus\tempo real`) | Lista passa a "Lista personalizada"; situação volta a "Ativo" |
| 21 | Copiar o arquivo de teste (passo 2) para essa pasta, com outro nome e extensão `.exe` | Em poucos segundos: aviso na bandeja "Ameaça detectada pelo tempo real"; linha na tabela com "Aguardando decisão"; contador de detecções sobe; nada é movido |
| 22 | Clicar no aviso da bandeja | Janela abre na tela Tempo real |
| 23 | Selecionar a linha → Mover para a quarentena… → Sim | Situação "Em quarentena"; arquivo some da pasta; item aparece na tela Quarentena |
| 24 | Copiar para a pasta um arquivo comum e um arquivo `.crdownload` | Contador "analisados" sobe só pelo arquivo comum; nenhuma detecção |
| 25 | Restaurar padrão | Volta às pastas padrão |
| 26 | Fechar a janela pelo X | Janela some; ícone continua na bandeja; na primeira vez, aviso "O Cocytus continua em execução" |
| 27 | Duplo clique no ícone da bandeja | Janela volta |
| 28 | Configurações → "Verificar com o Cocytus" → Adicionar; Iniciar com o Windows → Ativar | Ambos "Ativado para este Cocytus." |
| 29 | No Explorador, selecionar 3 arquivos da pasta do passo 2 (um deles o arquivo de teste) → botão direito → Mostrar mais opções → Verificar com o Cocytus | Uma única verificação com os 3 itens e 1 ameaça |
| 30 | Botão direito numa pasta e numa unidade (ex.: um pendrive) → Verificar com o Cocytus | Verificação da pasta / unidade inteira |
| 31 | `scripts\testar-integracao.cmd` | Log em `.dev\logs\integracao-*.log` com os comandos registrados; Cocytus faz uma verificação com 3 itens e 1 ameaça |
| 32 | Sair do Cocytus → reiniciar o Windows (ou sair e entrar de novo) | Cocytus inicia escondido na bandeja, tempo real ativo |
| 33 | Configurações → Remover e Desativar | Ambos "Desativado."; a opção some do menu do botão direito |

## Tipos de verificação, otimização e desinstalação

| # | Passo | Resultado esperado |
| --- | --- | --- |
| 34 | Verificar → Verificação completa | Mostra as unidades fixas (C:\, D:\…); Iniciar pede confirmação ("pode levar horas") |
| 35 | Verificar → Verificação rápida → Iniciar | "Levantando programas em execução…", depois a varredura; ao terminar, Histórico mostra "Verificação rápida: N programa(s)… " e as pastas |
| 36 | Verificar → Verificação selecionada → Adicionar pasta… (pasta descartável com o arquivo de teste) → Iniciar | 1 ameaça; as outras opções ficam desabilitadas durante a varredura |
| 37 | Otimização → Uso do computador | Discos com barra e espaço livre, memória, 10 programas por memória e por CPU; Atualizar mede de novo |
| 38 | Otimização → Limpeza → Analisar | Quantidade e tamanho por categoria; nada é apagado |
| 39 | Limpar… → Não | Nada muda |
| 40 | Otimização → Inicialização → selecionar o item Cocytus → Desativar → Ativar | Estado muda para Desativado e volta para Ativado; o item continua no registro (Configurações mostra o aviso enquanto estiver desativado) |
| 41 | Item de "todos os usuários" → Desativar | Aviso do UAC; recusar → "Alteração cancelada" |
| 42 | `scripts\criar-app-de-teste.cmd` → Desinstalar apps → buscar "Cocytus App" → Desinstalar… → Sim | Aguarda o desinstalador; "desinstalado"; painel "Restos de Cocytus App de Teste" com a pasta em `%LOCALAPPDATA%\Programs` e a chave `HKCU\Software\Cocytus App de Teste` |
| 43 | Remover selecionados… → Sim | Pasta "Na Lixeira"; chave "Removida (cópia: ….reg)"; a cópia fica em `%LocalAppData%\Cocytus\backups\registro` |
| 44 | Desmarcar "Mostrar apps da Microsoft Store" | Só programas na lista |

