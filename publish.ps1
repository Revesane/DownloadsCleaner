<#
.SYNOPSIS
  Собирает Downloads Cleaner: тесты + self-contained публикация для Windows x64 (один exe) + zip-архив.
.EXAMPLE
  .\publish.ps1            # тесты и публикация
  .\publish.ps1 -SkipTests # только публикация
#>
param(
    [switch]$SkipTests,
    [string]$Output = "publish\win-x64"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден dotnet. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

if (-not $SkipTests) {
    Write-Host "== Тесты ==" -ForegroundColor Cyan
    dotnet test tests\DownloadsCleaner.Tests\DownloadsCleaner.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Тесты не пройдены, публикация отменена." }
}

Write-Host "== Публикация (self-contained, win-x64) ==" -ForegroundColor Cyan
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
dotnet publish src\DownloadsCleaner\DownloadsCleaner.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась." }

$exe = Join-Path $Output "DownloadsCleaner.exe"
$zip = Join-Path (Split-Path $Output -Parent) "DownloadsCleaner-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $exe -DestinationPath $zip

# Установщик - если установлен Inno Setup 6 (https://jrsoftware.org/isinfo.php).
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$setup = $null
if ($iscc) {
    Write-Host "== Установщик ==" -ForegroundColor Cyan
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    & $iscc /Q "/DAppVersion=$version" "/DSourceExe=$((Resolve-Path $exe).Path)" "/O$(Split-Path $Output -Parent)" installer\DownloadsCleaner.iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с ошибкой $LASTEXITCODE" }
    $setup = Join-Path (Split-Path $Output -Parent) "DownloadsCleanerSetup.exe"
} else {
    Write-Host "Inno Setup 6 не найден - установщик пропущен." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Готово:" -ForegroundColor Green
Write-Host "  $((Resolve-Path $exe).Path)  ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) МБ)"
Write-Host "  $((Resolve-Path $zip).Path)"
if ($setup) { Write-Host "  $((Resolve-Path $setup).Path)" }
