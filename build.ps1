# Gera os arquivos de distribuição em dist\.
#   .\build.ps1              -> dist\UsageIACockpit-Portable.exe (arquivo único, não precisa instalar o .NET)
#   .\build.ps1 -Installer   -> também dist\UsageIACockpit-Setup-<versão>.exe (requer Inno Setup 6)
#
# Assinatura (opcional, requer o signtool do Windows SDK). Assina o exe portátil, o exe do app
# instalado e o instalador, com carimbo de tempo para a assinatura valer depois que o certificado vencer.
#   .\build.ps1 -CertThumbprint <SHA1>   -> certificado do repositório do Windows (Cert:\CurrentUser\My)
#   .\build.ps1 -PfxPath cert.pfx        -> arquivo .pfx; a senha vem da variável SIGN_PFX_PASSWORD
param(
    [switch]$Installer,
    [string]$Version,
    [string]$CertThumbprint,
    [string]$PfxPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'UsageCockpit\UsageCockpit.csproj'
$dist = Join-Path $root 'dist'
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=none', '-nologo')

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$signArgs = $null
if ($CertThumbprint -and $PfxPath) { throw 'Use -CertThumbprint ou -PfxPath, nao os dois' }
if ($CertThumbprint) {
    $signArgs = @('/sha1', $CertThumbprint)
} elseif ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "Certificado nao encontrado: $PfxPath" }
    $signArgs = @('/f', (Resolve-Path $PfxPath).Path)
    if ($env:SIGN_PFX_PASSWORD) { $signArgs += @('/p', $env:SIGN_PFX_PASSWORD) }
}
if ($signArgs) {
    $signtool = @(
        (Get-Command signtool -ErrorAction SilentlyContinue).Source
        Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | ForEach-Object FullName
    ) | Where-Object { $_ } | Select-Object -First 1
    if (-not $signtool) { throw 'signtool nao encontrado. Instale o Windows SDK: winget install Microsoft.WindowsSDK.10.0.26100' }
}

function Sign([string[]]$Files) {
    if (-not $signArgs) { return }
    & $signtool sign @signArgs /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'Usage IA Cockpit' @Files
    if ($LASTEXITCODE -ne 0) { throw 'signtool falhou' }
}

Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue

# Portátil: um único exe, sem compactação. Exe que se descompacta sozinho é um padrão que os
# antivírus tratam como suspeito (o Bitdefender acusava falso positivo), então o arquivo fica maior.
dotnet publish $project @common -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $dist 'portable')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (portatil) falhou' }
Move-Item (Join-Path $dist 'portable\UsageIACockpit.exe') (Join-Path $dist 'UsageIACockpit-Portable.exe')
Remove-Item -Recurse -Force (Join-Path $dist 'portable')
Sign (Join-Path $dist 'UsageIACockpit-Portable.exe')
Write-Host "Portatil: $dist\UsageIACockpit-Portable.exe"

if ($Installer) {
    $iscc = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 nao encontrado. Instale com: winget install JRSoftware.InnoSetup' }

    # Instalador: a pasta publicada (sem arquivo único) comprime melhor no lzma2 do Inno Setup,
    # e o app instalado abre mais rápido e usa menos memória que o exe único.
    $app = Join-Path $dist 'app'
    dotnet publish $project @common -p:Version=$Version -o $app
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (instalador) falhou' }
    Sign (Join-Path $app 'UsageIACockpit.exe'), (Join-Path $app 'UsageIACockpit.dll')

    & $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$dist" (Join-Path $root 'installer\UsageCockpit.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falhou' }
    Remove-Item -Recurse -Force $app
    Sign (Join-Path $dist "UsageIACockpit-Setup-$Version.exe")
    Write-Host "Instalador: $dist\UsageIACockpit-Setup-$Version.exe"
}
