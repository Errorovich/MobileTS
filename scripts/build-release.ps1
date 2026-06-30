#requires -Version 5
<#
.SYNOPSIS
    Релизная сборка MobileTS: отдельные APK под каждую архитектуру + универсальный.

.DESCRIPTION
    Гоняет `dotnet publish -c Release` и собирает готовые подписанные APK в каталог dist/
    с именами MobileTS-<версия>-<abi>.apk. Сужение до одной ABI делается через -p:AbiTarget
    (csproj подставляет его в RuntimeIdentifiers только для приложения, не задевая TSLib).

    Варианты:
      arm64     только arm64-v8a (реальные телефоны)
      x64       только x86_64 (эмуляторы)
      universal обе ABI в одном пакете (ставится куда угодно)

    Подпись берётся из MobileTS/signing.props (как и обычный Release). Без него — авто-debug-ключ.
    Версия читается из <ApplicationDisplayVersion> в csproj. versionCode у всех вариантов один:
    для сайдлоада это нормально (разные ABI-APK не стоят рядом — ставится один). Для Google Play
    multi-APK каждому варианту нужен СВОЙ versionCode — тогда добавь -p:ApplicationVersion=<n>.

.PARAMETER Targets
    Какие варианты собирать: all (по умолчанию), arm64, x64, universal. Можно несколько.

.PARAMETER Aot
    Включить AOT (RunAOTCompilation=true): быстрее старт, но APK заметно больше.

.EXAMPLE
    pwsh scripts/build-release.ps1                 # все три варианта
    pwsh scripts/build-release.ps1 -Targets arm64  # только arm64-v8a
    pwsh scripts/build-release.ps1 -Targets arm64,universal -Aot
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'arm64', 'x64', 'universal')]
    [string[]] $Targets = @('all'),
    [switch] $Aot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ABI (папки lib/<abi>/) внутри APK — для самопроверки, что вариант собрался под нужную архитектуру.
function Get-ApkAbis([string] $apkPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($apkPath)
    try {
        return $zip.Entries |
            Where-Object { $_.FullName -match '^lib/([^/]+)/' } |
            ForEach-Object { ($_.FullName -split '/')[1] } |
            Sort-Object -Unique
    }
    finally { $zip.Dispose() }
}

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'MobileTS\MobileTS.csproj'
$binBase = Join-Path $root 'MobileTS\bin\Release\net9.0-android'
$dist = Join-Path $root 'dist'

if (-not (Test-Path $proj)) { throw "Не найден проект: $proj" }

# Версия из csproj (<ApplicationDisplayVersion>0.1.1</ApplicationDisplayVersion>).
$csprojText = Get-Content $proj -Raw
$ver = [regex]::Match($csprojText, '<ApplicationDisplayVersion>\s*([^<]+?)\s*</ApplicationDisplayVersion>').Groups[1].Value
if ([string]::IsNullOrWhiteSpace($ver)) { $ver = '0.0.0' }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Матрица вариантов: ключ -> RID + имя ABI для файла.
# Одна ABI выбирается через -p:AbiTarget=<rid> (csproj подставляет его в RuntimeIdentifiers только
# для приложения, не задевая TSLib). Пустой Rid = «универсальный»: без AbiTarget берутся обе ABI.
$matrix = [ordered]@{
    arm64     = @{ Rid = 'android-arm64'; Abi = 'arm64-v8a'; Expect = @('arm64-v8a') }
    x64       = @{ Rid = 'android-x64';   Abi = 'x86_64';    Expect = @('x86_64') }
    universal = @{ Rid = '';              Abi = 'universal'; Expect = @('arm64-v8a', 'x86_64') }
}

$selected = if ($Targets -contains 'all') { @($matrix.Keys) } else { $Targets }
$aotArg = if ($Aot) { 'true' } else { 'false' }

Write-Host "MobileTS release build  |  версия $ver  |  AOT=$aotArg" -ForegroundColor Green
Write-Host "варианты: $($selected -join ', ')`n"

$results = @()
foreach ($key in $selected) {
    $m = $matrix[$key]
    $singleAbi = -not [string]::IsNullOrEmpty($m.Rid)

    $ridLabel = if ($singleAbi) { $m.Rid } else { 'android-arm64;android-x64 (из csproj)' }
    Write-Host "==== $key ($($m.Abi)) : $ridLabel ====" -ForegroundColor Cyan

    # Одна ABI -> -p:AbiTarget=<rid> (csproj подставит его в RuntimeIdentifiers только для приложения).
    # Универсальный -> без аргумента, обе ABI берутся из дефолтного RuntimeIdentifiers.
    $publishArgs = @($proj, '-c', 'Release', '-f', 'net9.0-android',
        '-p:AndroidPackageFormat=apk', "-p:RunAOTCompilation=$aotArg")
    if ($singleAbi) { $publishArgs += "-p:AbiTarget=$($m.Rid)" }

    dotnet publish @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Сборка варианта '$key' упала (exit $LASTEXITCODE)." }

    # Все варианты пишут подписанный APK в bin/.../publish (одиночное значение RuntimeIdentifiers
    # не создаёт RID-подкаталог). Сборки идут последовательно и каждая перезаписывает его, поэтому
    # берём самый свежий из этого каталога.
    $publishDir = Join-Path $binBase 'publish'
    $apk = Get-ChildItem $publishDir -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apk) { throw "APK не найден в $publishDir (вариант '$key')." }

    # Самопроверка: вариант должен содержать ровно ожидаемые ABI (иначе сужение не сработало).
    $gotAbis = @(Get-ApkAbis $apk.FullName)
    $expected = @($m.Expect)
    $diff = Compare-Object $expected $gotAbis
    if ($diff) {
        throw "Вариант '$key': ожидались ABI [$($expected -join ', ')], получены [$($gotAbis -join ', ')] в $($apk.Name)."
    }

    $destName = "MobileTS-$ver-$($m.Abi).apk"
    Copy-Item $apk.FullName (Join-Path $dist $destName) -Force

    $results += [pscustomobject]@{
        Variant = $key
        ABI     = $m.Abi
        'MB'    = [math]::Round($apk.Length / 1MB, 2)
        File    = $destName
    }
}

Write-Host "`n==== готово -> $dist ====" -ForegroundColor Green
$results | Format-Table -AutoSize
