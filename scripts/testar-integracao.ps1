#requires -Version 5.1
<#
.SYNOPSIS
  Teste da integração com o Windows: lê do registro (HKCU) o comando de "Verificar com o Cocytus" e o de início
  automático, e executa o comando do menu do mesmo jeito que o Explorer faria ao selecionar vários itens
  (um processo por item, quase ao mesmo tempo). Os itens são arquivos descartáveis em .dev\teste-integracao.
  Não altera o registro.
#>
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$logDir = Join-Path $ProjectRoot '.dev\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("integracao-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
function Log([string]$m) { $l = "[$(Get-Date -Format 'HH:mm:ss')] $m"; Write-Host $l; Add-Content -LiteralPath $log -Value $l -Encoding UTF8 }

# Atenção: variáveis do PowerShell não diferenciam maiúsculas; não reutilizar o nome da raiz do projeto aqui.
foreach ($classKey in '*', 'Directory', 'Drive') {
    $key = "HKCU:\Software\Classes\$classKey\shell\Cocytus.Verificar"
    if (Test-Path $key) {
        $verb = Get-ItemProperty -LiteralPath $key
        $cmd = (Get-ItemProperty -LiteralPath "$key\command").'(default)'
        Log "Menu [$classKey]: '$($verb.'(default)')' MultiSelectModel=$($verb.MultiSelectModel) Icon=$($verb.Icon)"
        Log "  comando: $cmd"
    }
    else {
        Log "Menu [$classKey]: não instalado"
    }
}
$run = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Cocytus
Log "Início com o Windows: $(if ($run) { $run } else { 'desativado' })"

$template = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\*\shell\Cocytus.Verificar\command' -ErrorAction SilentlyContinue).'(default)'
if (-not $template) { Log 'Sem comando de menu registrado; nada a executar.'; exit 1 }

$testDir = Join-Path $ProjectRoot '.dev\teste-integracao\seleção com espaço'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$files = @(
    (Join-Path $testDir 'primeiro arquivo ç.txt'),
    (Join-Path $testDir 'segundo-arquivo-de-teste.txt'),
    (Join-Path $testDir 'terceiro é.txt'))
Set-Content -LiteralPath $files[0] -Value 'COCYTUS-ARQUIVO-DE-TESTE-INOFENSIVO teste do menu' -Encoding ASCII
Set-Content -LiteralPath $files[1] -Value 'conteúdo comum' -Encoding UTF8
Set-Content -LiteralPath $files[2] -Value 'outro conteúdo comum' -Encoding UTF8

# Separa executável e argumentos do comando registrado, como o Explorer faz.
if ($template -notmatch '^"(?<exe>[^"]+)"\s*(?<args>.*)$') { Log "Formato de comando inesperado: $template"; exit 1 }
$exe = $Matches.exe
foreach ($file in $files) {
    $arguments = $Matches.args.Replace('%1', $file)
    Log "Executando: `"$exe`" $arguments"
    Start-Process -FilePath $exe -ArgumentList $arguments
}
Log 'Pronto. Confira na janela do Cocytus: uma única verificação com os 3 itens e 1 ameaça (arquivo de teste).'
