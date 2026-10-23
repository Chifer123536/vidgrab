param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$HostName = 'com.vidgrab.host'
$ExtId = 'ieabnpdefllgobhgbacnbihemckkhkjo'
$Src = $PSScriptRoot
$Root = Join-Path $env:LOCALAPPDATA 'VidGrab'
$Bin = Join-Path $Root 'bin'
$HostDir = Join-Path $Root 'host'
$ExtDir = Join-Path $Root 'extension'
$Setup = Join-Path $Root 'tmp\setup'

$Urls = @{
    YtDlp  = 'https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe'
    Deno   = 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip'
    FFmpeg = 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip'
}

function Step([string]$Text) { Write-Host "`n> $Text" -ForegroundColor Cyan }
function Ok([string]$Text) { Write-Host "  $Text" -ForegroundColor Green }

function Get-File([string]$Url, [string]$OutFile) {
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (Test-Path $curl) {
        Invoke-Native { & $curl -L --fail --retry 3 --retry-delay 2 --connect-timeout 20 -# -o $OutFile $Url }
        if ($LASTEXITCODE -ne 0) { throw "Не удалось скачать $Url (curl $LASTEXITCODE)" }
    }
    else {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $OutFile
    }
    if (-not (Test-Path $OutFile) -or (Get-Item $OutFile).Length -lt 1024) { throw "Пустой файл: $Url" }
}

function Expand-Zip([string]$Zip, [string]$Dest) {
    New-Item -ItemType Directory -Force $Dest | Out-Null
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (Test-Path $tar) {
        Invoke-Native { & $tar -xf $Zip -C $Dest }
        if ($LASTEXITCODE -eq 0) { return }
    }
    Expand-Archive -Path $Zip -DestinationPath $Dest -Force
}

function Invoke-Native([scriptblock]$Block) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $prev }
}

function Find-Browser([string[]]$Candidates) {
    foreach ($c in $Candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Test-Loaded([string]$UserData) {
    if (-not (Test-Path $UserData)) { return $false }
    $files = Get-ChildItem $UserData -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'Secure Preferences'; Join-Path $_.FullName 'Preferences' } |
        Where-Object { Test-Path $_ }
    foreach ($f in $files) {
        try { if ((Get-Content $f -Raw -ErrorAction Stop).Contains($ExtId)) { return $true } } catch { }
    }
    return $false
}

try {
    if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'Нужна Windows 10 или новее.' }
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Нужна 64-битная Windows.' }
    if (-not (Test-Path (Join-Path $Src 'extension\manifest.json'))) { throw "Не найдена папка extension рядом с install.ps1 ($Src)." }

    Step 'Останавливаю старый помощник'
    Get-Process vidgrab-host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    New-Item -ItemType Directory -Force $Root, $Bin, $HostDir, $Setup | Out-Null

    Step 'Копирую расширение'
    $srcExt = (Resolve-Path (Join-Path $Src 'extension')).Path.TrimEnd('\')
    if ($srcExt -ne $ExtDir.TrimEnd('\')) {
        if (Test-Path $ExtDir) { Remove-Item $ExtDir -Recurse -Force }
        Copy-Item $srcExt $ExtDir -Recurse -Force
    }
    Get-ChildItem $ExtDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    Ok $ExtDir

    Step 'Собираю помощник'
    $csc = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
    if (-not (Test-Path $csc)) { throw "Не найден компилятор .NET Framework: $csc" }
    $exe = Join-Path $HostDir 'vidgrab-host.exe'
    $cs = Join-Path $Src 'host\VidGrabHost.cs'
    $log = Invoke-Native { & $csc /nologo /target:exe /optimize+ /out:$exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll $cs 2>&1 }
    if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw 'Сборка помощника не удалась.' }
    Ok $exe

    $manifestPath = Join-Path $HostDir "$HostName.json"
    $manifest = [ordered]@{
        name            = $HostName
        description     = 'VidGrab helper (yt-dlp)'
        path            = $exe
        type            = 'stdio'
        allowed_origins = @("chrome-extension://$ExtId/")
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

    Step 'Регистрирую помощник для Chrome и Яндекс Браузера'
    foreach ($base in 'HKCU:\Software\Google\Chrome', 'HKCU:\Software\Chromium', 'HKCU:\Software\Microsoft\Edge') {
        $key = "$base\NativeMessagingHosts\$HostName"
        New-Item -Path $key -Force | Out-Null
        Set-Item -Path $key -Value $manifestPath
    }
    Ok 'готово'

    $ytdlp = Join-Path $Bin 'yt-dlp.exe'
    if (Test-Path $ytdlp) {
        Step 'Обновляю yt-dlp'
        Invoke-Native { & $ytdlp --ignore-config --update-to nightly 2>&1 | Out-Null }
    }
    else {
        Step 'Скачиваю yt-dlp'
        Get-File $Urls.YtDlp $ytdlp
    }

    if (-not (Test-Path (Join-Path $Bin 'deno.exe'))) {
        Step 'Скачиваю Deno (нужен yt-dlp для YouTube)'
        $zip = Join-Path $Setup 'deno.zip'
        Get-File $Urls.Deno $zip
        Expand-Zip $zip (Join-Path $Setup 'deno')
        Copy-Item (Join-Path $Setup 'deno\deno.exe') $Bin -Force
    }

    if (-not (Test-Path (Join-Path $Bin 'ffmpeg.exe'))) {
        Step 'Скачиваю ffmpeg (~90 МБ)'
        $zip = Join-Path $Setup 'ffmpeg.zip'
        Get-File $Urls.FFmpeg $zip
        Expand-Zip $zip (Join-Path $Setup 'ffmpeg')
        $ffBin = Get-ChildItem (Join-Path $Setup 'ffmpeg') -Recurse -Filter ffmpeg.exe | Select-Object -First 1
        if (-not $ffBin) { throw 'В архиве ffmpeg не найден ffmpeg.exe' }
        Get-ChildItem $ffBin.DirectoryName -File |
            Where-Object { $_.Name -notlike 'ffplay*' } |
            Copy-Item -Destination $Bin -Force
    }

    $ver = Invoke-Native { & $ytdlp --ignore-config --version 2>$null | Select-Object -First 1 }
    if (-not $ver) { throw 'yt-dlp не запускается. Проверь, не удалил ли его антивирус.' }
    [IO.File]::WriteAllText((Join-Path $Bin 'ytdlp.stamp'), $ver.Trim())
    Ok "yt-dlp $ver"

    Remove-Item $Setup -Recurse -Force -ErrorAction SilentlyContinue

    $browsers = @(
        @{
            Name     = 'Chrome'
            Exe      = Find-Browser @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe")
            UserData = "$env:LOCALAPPDATA\Google\Chrome\User Data"
            Page     = 'chrome://extensions/'
        },
        @{
            Name     = 'Яндекс Браузер'
            Exe      = Find-Browser @("$env:LOCALAPPDATA\Yandex\YandexBrowser\Application\browser.exe", "$env:ProgramFiles\Yandex\YandexBrowser\Application\browser.exe")
            UserData = "$env:LOCALAPPDATA\Yandex\YandexBrowser\User Data"
            Page     = 'browser://extensions/'
        }
    )

    $missing = @($browsers | Where-Object { $_.Exe -and -not (Test-Loaded $_.UserData) })
    $loaded = @($browsers | Where-Object { $_.Exe -and (Test-Loaded $_.UserData) })

    Write-Host ''
    Write-Host '=============================================' -ForegroundColor DarkGray
    Write-Host ' VidGrab установлен' -ForegroundColor Green
    Write-Host '=============================================' -ForegroundColor DarkGray

    foreach ($b in $loaded) { Write-Host " $($b.Name): расширение уже подключено, обновится само." }

    if ($missing.Count -gt 0) {
        try { Set-Clipboard -Value $ExtDir } catch { }
        Write-Host ''
        Write-Host ' Осталось подключить расширение (один раз):' -ForegroundColor Yellow
        Write-Host '  1. На странице расширений включи «Режим разработчика».'
        Write-Host '  2. Нажми «Загрузить распакованное».'
        Write-Host '  3. Вставь путь (он уже в буфере обмена) и нажми «Выбор папки»:'
        Write-Host "     $ExtDir" -ForegroundColor White
        if (-not $NoBrowser) {
            foreach ($b in $missing) {
                try { Start-Process -FilePath $b.Exe -ArgumentList $b.Page } catch { }
            }
        }
    }
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Ошибка: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
