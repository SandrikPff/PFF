param([switch]$Setup, [switch]$Launch, [string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'CrimsonPanel\CrimsonPanel.csproj'
$publish = Join-Path $PSScriptRoot 'publish'
$exe = Join-Path $publish 'PFF.exe'

$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) {
    throw '.NET SDK was not found. Install it with: winget install --exact --id Microsoft.DotNet.SDK.8'
}

function Find-InnoCompiler {
    if ($InnoCompiler) {
        $file = Get-Item -LiteralPath $InnoCompiler
        if ($file.PSIsContainer -or $file.Name -ine 'ISCC.exe') {
            throw 'Pass the full path to ISCC.exe with -InnoCompiler.'
        }
        return $file.FullName
    }

    $onPath = (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    $candidates = @(
        $onPath
        foreach ($baseDir in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, "$env:LOCALAPPDATA\Programs")) {
            if ($baseDir) {
                Join-Path $baseDir 'Inno Setup 6\ISCC.exe'
                Join-Path $baseDir 'Inno Setup 7\ISCC.exe'
            }
        }
        foreach ($registryRoot in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
            foreach ($entry in Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue) {
                $installation = Get-ItemProperty -LiteralPath $entry.PSPath -ErrorAction SilentlyContinue
                if ($installation.DisplayName -notmatch '^Inno Setup\b') { continue }
                foreach ($directory in @($installation.'Inno Setup: App Path', $installation.InstallLocation)) {
                    if ($directory) { Join-Path ($directory.Trim('"')) 'ISCC.exe' }
                }
            }
        }
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $path
        }
    }
}

$compiler = $null
if ($Setup) {
    $compiler = Find-InnoCompiler
    if (-not $compiler) {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) { throw 'ISCC.exe not found. Install Inno Setup or pass -InnoCompiler with the full path to ISCC.exe.' }
        & $winget.Source install --id JRSoftware.InnoSetup --exact --source winget --scope machine --silent --accept-package-agreements --accept-source-agreements
        $wingetExit = $LASTEXITCODE
        $compiler = Find-InnoCompiler
        if (-not $compiler) {
            throw "ISCC.exe not found after checking Inno Setup (winget exit code $wingetExit). Pass -InnoCompiler with the full path to ISCC.exe."
        }
    }
}

& $dotnet.Source publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'PFF.exe was not created.' }

if ($Setup) {
    $iss = Join-Path $PSScriptRoot 'Installer\PFF.iss'
    Write-Host "Using Inno Setup: $compiler"
    & $compiler $iss
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)." }
    $exe = Join-Path $PSScriptRoot 'Installer\output\PFF_Setup.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The installer was not created.' }
}
Write-Host "Built: $exe" -ForegroundColor Green
if ($Launch) { Start-Process -FilePath $exe -Verb RunAs }
