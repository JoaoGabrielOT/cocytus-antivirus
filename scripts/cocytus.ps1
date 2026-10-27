#requires -Version 5.1
<#
.SYNOPSIS
  Tarefas de desenvolvimento do Cocytus: preparar ambiente, compilar, testar e gerar o build.
.DESCRIPTION
  Tudo fica dentro da pasta do projeto:
    .tools\dotnet      SDK do .NET 10 (instalado pelo script oficial dotnet-install.ps1, sem instalação global)
    engines\clamav     binários oficiais do ClamAV para Windows x64
    .cache\            pacotes NuGet, caches do SDK e downloads
    .dev\data          dados de desenvolvimento (bases do ClamAV, quarentena, histórico)
    .dev\logs          logs de cada execução
  Tarefas: sdk, engines, db, build, format, test, publish, package, prepare (sdk+engines+db),
           verify (build+format+test), all (prepare+verify+publish),
           release (sdk+engines+build+format+test+publish+package; usada pelo GitHub Actions).
  -Version 1.2.3 grava essa versão nos assemblies (senão vale a do Directory.Build.props).
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('sdk', 'engines', 'db', 'build', 'format', 'test', 'publish', 'package', 'prepare', 'verify', 'all', 'release')]
    [string]$Task,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$Root = Split-Path $PSScriptRoot -Parent
$ToolsDir = Join-Path $Root '.tools'
$DotnetDir = Join-Path $ToolsDir 'dotnet'
$Dotnet = Join-Path $DotnetDir 'dotnet.exe'
$CacheDir = Join-Path $Root '.cache'
$DevDir = Join-Path $Root '.dev'
$LogsDir = Join-Path $DevDir 'logs'
$DevData = Join-Path $DevDir 'data'
$EnginesDir = Join-Path $Root 'engines\clamav'
$Solution = Join-Path $Root 'Cocytus.slnx'
$DistDir = Join-Path $Root 'dist\Cocytus'
$ReleaseDir = Join-Path $Root 'dist\release'
$VersionArgs = @()
if ($Version) { $VersionArgs = @("-p:Version=$Version") }

New-Item -ItemType Directory -Force -Path $LogsDir, $CacheDir, $DevData | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:LogFile = Join-Path $LogsDir "$Task-$stamp.log"
$script:StatusFile = Join-Path $LogsDir 'ultimo-status.txt'
Set-Content -LiteralPath $script:StatusFile -Value "tarefa=$Task inicio=$(Get-Date -Format s)" -Encoding UTF8

# Caches e configurações do SDK dentro do projeto (nada em %USERPROFILE%\.nuget)
$env:DOTNET_ROOT = $DotnetDir
$env:DOTNET_CLI_HOME = Join-Path $CacheDir 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $CacheDir 'nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $CacheDir 'nuget\http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $CacheDir 'nuget\plugins'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:PATH = "$DotnetDir;$env:PATH"

function Write-Log([string]$Message) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $Message"
    Write-Host $line
    Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
}

function Add-Status([string]$Line) {
    Add-Content -LiteralPath $script:StatusFile -Value $Line -Encoding UTF8
}

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    Write-Log "> $Exe $($Arguments -join ' ')"
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object {
            $text = "$_"
            # Barra de progresso do freshclam: não vai para o log.
            if ($text -match '^\s*Time:\s') { return }
            Write-Host $text
            Add-Content -LiteralPath $script:LogFile -Value $text -Encoding UTF8
        }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    Write-Log "< código de saída: $code"
    return $code
}

function Assert-Sdk {
    if (-not (Test-Path $Dotnet)) { throw "SDK não encontrado em $DotnetDir. Rode a tarefa 'sdk' (scripts\preparar-ambiente.cmd)." }
}

function Step-Sdk {
    $installer = Join-Path $ToolsDir 'dotnet-install.ps1'
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    Write-Log 'Baixando o script oficial dotnet-install.ps1 (https://dot.net/v1/dotnet-install.ps1)'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    $sig = Get-AuthenticodeSignature -FilePath $installer
    Write-Log "Assinatura do dotnet-install.ps1: $($sig.Status) / $($sig.SignerCertificate.Subject)"
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw 'Assinatura do dotnet-install.ps1 inválida ou de outro emissor; instalação cancelada.'
    }
    $code = Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer,
        '-Channel', '10.0', '-InstallDir', $DotnetDir, '-NoPath')
    if ($code -ne 0) { throw "dotnet-install.ps1 falhou ($code)" }
    $code = Invoke-Native $Dotnet @('--info')
    if ($code -ne 0) { throw 'dotnet --info falhou' }
}

function Step-Engines {
    $headers = @{ 'User-Agent' = 'Cocytus-Setup'; 'Accept' = 'application/vnd.github+json' }
    # No GitHub Actions, o token do próprio workflow evita o limite de consultas anônimas.
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    Write-Log 'Consultando as versões oficiais do ClamAV (github.com/Cisco-Talos/clamav)'
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/Cisco-Talos/clamav/releases?per_page=20' -Headers $headers
    $chosen = $null
    foreach ($release in ($releases | Where-Object { -not $_.prerelease -and -not $_.draft })) {
        $asset = $release.assets | Where-Object { $_.name -match '^clamav-[\d.]+\.win\.x64\.zip$' } | Select-Object -First 1
        if ($asset) { $chosen = [pscustomobject]@{ Release = $release; Asset = $asset }; break }
    }
    if (-not $chosen) { throw 'Nenhuma versão estável recente do ClamAV publica clamav-*.win.x64.zip' }

    $downloads = Join-Path $CacheDir 'downloads'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    $zip = Join-Path $downloads $chosen.Asset.name
    Write-Log "Baixando $($chosen.Asset.name) ($($chosen.Release.tag_name), $([math]::Round($chosen.Asset.size / 1MB, 1)) MB)"
    Invoke-WebRequest -Uri $chosen.Asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'Cocytus-Setup' }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Log "SHA-256 do arquivo baixado: $hash"
    $digest = $chosen.Asset.digest
    if ($digest) {
        Write-Log "SHA-256 publicado pelo GitHub: $digest"
        if ($digest -ne "sha256:$hash") { throw 'O SHA-256 do download não confere com o publicado. Abortado.' }
    }
    else {
        Write-Log 'Aviso: o GitHub não publicou digest para este arquivo; integridade conferida só pelo TLS e pela assinatura dos executáveis.'
    }

    $work = Join-Path $CacheDir ("extract-" + [guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $zip -DestinationPath $work
        $clamd = Get-ChildItem -LiteralPath $work -Recurse -Filter 'clamd.exe' | Select-Object -First 1
        if (-not $clamd) { throw 'clamd.exe não encontrado dentro do pacote' }
        $sig = Get-AuthenticodeSignature -FilePath $clamd.FullName
        Write-Log "Assinatura do clamd.exe: $($sig.Status) / $($sig.SignerCertificate.Subject)"
        if (Test-Path $EnginesDir) { Remove-Item -LiteralPath $EnginesDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $EnginesDir | Out-Null
        Copy-Item -Path (Join-Path $clamd.DirectoryName '*') -Destination $EnginesDir -Recurse
        Set-Content -LiteralPath (Join-Path $EnginesDir 'VERSION.txt') -Encoding UTF8 -Value @(
            "$($chosen.Release.tag_name) ($($chosen.Asset.name))",
            "sha256=$hash",
            "origem=$($chosen.Asset.browser_download_url)",
            "assinatura_clamd=$($sig.Status) $($sig.SignerCertificate.Subject)")
        Write-Log "ClamAV instalado em $EnginesDir"
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Step-Db {
    $freshclam = Join-Path $EnginesDir 'freshclam.exe'
    if (-not (Test-Path $freshclam)) { throw 'freshclam.exe não encontrado. Rode a tarefa engines.' }
    $dbDir = Join-Path $DevData 'clamav-db'
    $confDir = Join-Path $DevData 'config'
    New-Item -ItemType Directory -Force -Path $dbDir, $confDir, (Join-Path $DevData 'logs') | Out-Null
    $conf = Join-Path $confDir 'freshclam-dev.conf'
    Set-Content -LiteralPath $conf -Encoding ASCII -Value @(
        "DatabaseDirectory `"$dbDir`"",
        "UpdateLogFile `"$(Join-Path $DevData 'logs\freshclam-dev.log')`"",
        'LogTime yes',
        'DatabaseMirror database.clamav.net',
        'DNSDatabaseInfo current.cvd.clamav.net',
        'ConnectTimeout 30',
        'TestDatabases yes',
        'Bytecode yes')
    $code = Invoke-Native $freshclam @("--config-file=$conf", '--stdout')
    if ($code -ne 0 -and $code -ne 1) { throw "freshclam falhou com código $code" }
}

function Step-Build {
    Assert-Sdk
    $code = Invoke-Native $Dotnet (@('build', $Solution, '-c', 'Release', '-nologo') + $VersionArgs)
    if ($code -ne 0) { throw 'Falha na compilação' }
}

function Step-Format {
    Assert-Sdk
    $code = Invoke-Native $Dotnet @('format', $Solution, '--verify-no-changes', '--verbosity', 'normal')
    if ($code -ne 0) { throw 'dotnet format encontrou diferenças de formatação/estilo' }
}

function Step-Test {
    Assert-Sdk
    $env:COCYTUS_TEST_ENGINES = $EnginesDir
    $env:COCYTUS_TEST_OFFICIAL_DB = Join-Path $DevData 'clamav-db'
    $env:COCYTUS_TEST_TMP = Join-Path $DevDir 'test-tmp'
    New-Item -ItemType Directory -Force -Path $env:COCYTUS_TEST_TMP | Out-Null
    $results = Join-Path $DevDir 'TestResults'
    $code = Invoke-Native $Dotnet @('test', $Solution, '-c', 'Release', '--no-build', '--results-directory', $results,
        '--logger', 'trx;LogFileName=cocytus-tests.trx', '--logger', 'console;verbosity=normal')
    if ($code -ne 0) { throw 'Há testes falhando' }
}

function Step-Publish {
    Assert-Sdk
    if (Test-Path $DistDir) { Remove-Item -LiteralPath $DistDir -Recurse -Force }
    $code = Invoke-Native $Dotnet (@('publish', (Join-Path $Root 'src\Cocytus.App\Cocytus.App.csproj'), '-c', 'Release',
        '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-o', $DistDir, '-nologo') + $VersionArgs)
    if ($code -ne 0) { throw 'Falha no publish' }
    if (Test-Path $EnginesDir) {
        $target = Join-Path $DistDir 'engines\clamav'
        # Símbolos (.pdb), bibliotecas de link (.lib), cabeçalhos e manual (~800 MB) ficam de fora do pacote.
        $skipExt = @('.pdb', '.lib')
        $files = Get-ChildItem -LiteralPath $EnginesDir -Recurse -File | Where-Object {
            ($skipExt -notcontains $_.Extension.ToLowerInvariant()) -and ($_.FullName -notmatch '\\(include|UserManual)\\')
        }
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($EnginesDir.Length).TrimStart('\')
            $destination = Join-Path $target $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        Write-Log "Motor copiado: $($files.Count) arquivos"
    }
    else {
        Write-Log 'Aviso: engines\clamav não existe; o build sai sem motor.'
    }
    foreach ($doc in 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        $p = Join-Path $Root $doc
        if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination $DistDir }
    }
    Write-Log "Build pronto em $DistDir"
}

function Get-ProjectVersion {
    if ($Version) { return $Version }
    [xml]$props = Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw
    return ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}

function Step-Package {
    if (-not (Test-Path (Join-Path $DistDir 'Cocytus.exe'))) { throw 'dist\Cocytus não existe. Rode a tarefa publish.' }
    $v = Get-ProjectVersion
    New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
    $name = "Cocytus-$v-win-x64.zip"
    $zip = Join-Path $ReleaseDir $name
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    # Os arquivos ficam na raiz do zip (Cocytus.exe, engines\clamav\...).
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($DistDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $name" -Encoding ASCII -NoNewline
    Write-Log "Pacote: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB), SHA-256 $hash"
}

$steps = switch ($Task) {
    'prepare' { @('sdk', 'engines', 'db') }
    'verify' { @('build', 'format', 'test') }
    'all' { @('sdk', 'engines', 'db', 'build', 'format', 'test', 'publish') }
    'release' { @('sdk', 'engines', 'build', 'format', 'test', 'publish', 'package') }
    default { @($Task) }
}

$failed = $false
foreach ($step in $steps) {
    Write-Log "=== $step ==="
    try {
        switch ($step) {
            'sdk' { Step-Sdk }
            'engines' { Step-Engines }
            'db' { Step-Db }
            'build' { Step-Build }
            'format' { Step-Format }
            'test' { Step-Test }
            'publish' { Step-Publish }
            'package' { Step-Package }
        }
        Add-Status "$step=ok"
    }
    catch {
        Write-Log "ERRO em ${step}: $($_.Exception.Message)"
        Add-Status "$step=FALHOU $($_.Exception.Message)"
        $failed = $true
        break
    }
}

Add-Status "fim=$(Get-Date -Format s) log=$script:LogFile"
if ($failed) { Write-Log 'Concluído com falhas.'; exit 1 }
Write-Log 'Concluído sem falhas.'
exit 0
