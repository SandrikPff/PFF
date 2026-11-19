param([switch]$Check, [string]$ErrorLog)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$work = $null
$stage = 'проверка установленного драйвера'
$exitCode = 0

function Get-CableDevice {
    Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPClass = 'MEDIA'" |
        Where-Object {
            $_.Present -ne $false -and ($_.HardwareID -contains 'VBAUDIOVACWDM' -or $_.Name -eq 'VB-Audio Virtual Cable')
        } |
        Select-Object -First 1
}

try {
    $device = Get-CableDevice
    if ($device) {
        if ($device.ConfigManagerErrorCode -eq 14) { exit 3010 }
        if ($device.ConfigManagerErrorCode -ne 0) {
            throw "VB-CABLE уже установлен. Диспетчер устройств сообщает код $($device.ConfigManagerErrorCode)."
        }
        [Console]::WriteLine('VB-CABLE уже установлен.')
        exit 0
    }
    if ($Check) { exit 10 }

    $stage = 'подготовка папки'
    $work = Join-Path ([IO.Path]::GetTempPath()) ('PFF-Cable-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null

    $stage = 'загрузка с сайта VB-Audio'
    $archive = Join-Path $work 'VB-CABLE.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip' -TimeoutSec 60 -OutFile $archive

    $stage = 'распаковка'
    Expand-Archive -LiteralPath $archive -DestinationPath $work
    $candidates = @(Get-ChildItem -LiteralPath $work -Recurse -File -Filter VBCABLE_Setup_x64.exe)
    if ($candidates.Count -ne 1) { throw 'В архиве должен быть один установщик VBCABLE_Setup_x64.exe.' }
    $installer = $candidates[0]

    $stage = 'проверка подписи'
    $signature = Get-AuthenticodeSignature -LiteralPath $installer.FullName
    if ($signature.Status -ne 'Valid') {
        throw "Windows не подтвердила подпись ($($signature.Status)). $($signature.StatusMessage)"
    }
    $publisher = $signature.SignerCertificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($publisher -notmatch '(?i)^(?:(?:vincent\s+burel|burel\s+vincent)(?:\s+entrepreneur\s+individuel)?|vb[- ]?audio(?:\s+software)?)$') {
        throw "Неожиданный издатель установщика: $publisher."
    }

    $stage = 'запуск мастера установки'
    $process = Start-Process -FilePath $installer.FullName -WorkingDirectory $installer.DirectoryName -Verb RunAs -Wait -PassThru
    try {
        $stage = 'завершение мастера установки'
        if ($process.ExitCode -notin @(0, 3010)) { throw "Мастер VB-CABLE вернул код $($process.ExitCode)." }
        $restartRequired = $process.ExitCode -eq 3010
    }
    finally {
        $process.Dispose()
    }
    $stage = 'проверка результата установки'
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $device = Get-CableDevice
        if ($device) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $device -and -not $restartRequired) {
        throw 'VB-CABLE не обнаружен. В мастере нажмите Install Driver и дождитесь окончания установки.'
    }
    if ($device -and $device.ConfigManagerErrorCode -notin @(0, 14)) {
        throw "Диспетчер устройств сообщает код $($device.ConfigManagerErrorCode)."
    }
    [Console]::WriteLine('VB-CABLE установлен. Перезагрузите Windows.')
    $exitCode = 3010
}
catch {
    $message = "VB-CABLE, ${stage}: $($_.Exception.Message)"
    [Console]::Error.WriteLine($message)
    if ($ErrorLog) {
        try { [IO.File]::WriteAllText($ErrorLog, $message, [System.Text.UTF8Encoding]::new($true)) }
        catch { [Console]::Error.WriteLine($_.Exception.Message) }
    }
    $exitCode = 1
}
finally {
    if ($work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
