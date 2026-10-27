# Componentes de terceiros

Todos gratuitos. Nenhum exige pagamento, chave ou cadastro para o uso feito pelo Cocytus.

| Componente | Versão | Licença | Como é usado | Obrigações |
| --- | --- | --- | --- | --- |
| ClamAV (`clamd.exe`, `freshclam.exe` e DLLs) | 1.5.4 (win x64, release oficial da Cisco Talos) | GPL-2.0 (e licenças dos componentes listados em `engines\clamav\COPYING`) | Processo separado; o Cocytus conversa com o `clamd` por TCP em 127.0.0.1 | Ao redistribuir os binários: manter os avisos de licença (`COPYING*`) e oferecer o código-fonte correspondente (https://github.com/Cisco-Talos/clamav, tag `clamav-1.5.4`) |
| Bases oficiais do ClamAV (`main`, `daily`, `bytecode`) | Atualizadas pelo usuário | Distribuídas gratuitamente pela Cisco Talos; uso sujeito aos termos de `database.clamav.net` | Baixadas em tempo de execução pelo `freshclam`; não são redistribuídas no pacote | Respeitar os limites de download do servidor (o freshclam já faz isso) |
| .NET 10 (runtime, WPF e Windows Forms) | 10.0.x | MIT | Runtime incluído no build autocontido; Windows Forms só para o ícone da bandeja (`NotifyIcon`) | Manter o aviso de licença do .NET |
| Projeções do Windows SDK (`Microsoft.Windows.SDK.NET.dll`) e `WinRT.Runtime.dll` (C#/WinRT) | 10.0.19041 (referência automática do .NET para `net10.0-windows10.0.19041.0`) | MIT | API `PackageManager` do Windows para listar e remover apps da Microsoft Store | Manter o aviso de licença |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | Histórico local | Manter o aviso de licença |
| SQLitePCLRaw (bundle_e_sqlite3) | 2.1.13 (dependência do Microsoft.Data.Sqlite) | Apache-2.0 | Ponte para o SQLite nativo | Manter o aviso de licença |
| SQLite | embutido no SQLitePCLRaw | Domínio público | Banco do histórico | — |
| xUnit, Microsoft.NET.Test.Sdk | 2.9.3 / 17.12.0 | Apache-2.0 / MIT | Só nos testes, não vão no build | — |

## Rede

- `scripts\cocytus.ps1` (só no desenvolvimento): baixa o `dotnet-install.ps1` (assinatura Microsoft conferida), o SDK do .NET,
  o ClamAV do GitHub (SHA-256 conferido com o digest publicado) e os pacotes NuGet.
- O aplicativo: acessa a rede apenas quando o usuário clica em **Atualizar bases** (`database.clamav.net`). Nenhum arquivo,
  hash, caminho ou dado de uso é enviado.

## Observação sobre os binários do ClamAV

O `clamd.exe` da release oficial 1.5.4 para Windows não traz assinatura Authenticode (verificado por
`Get-AuthenticodeSignature`: `NotSigned`). A integridade do pacote foi conferida pelo SHA-256 publicado pelo GitHub
(`0d9e0228b2674137ea1a2853566c98a0278ad52ab2582c3d6dbd75373848c395`), registrado em `engines\clamav\VERSION.txt`.
