$ErrorActionPreference = 'Stop'

$HostName = 'com.vidgrab.host'
$Root = Join-Path $env:LOCALAPPDATA 'VidGrab'

try {
    Get-Process vidgrab-host, yt-dlp -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300

    foreach ($base in 'HKCU:\Software\Google\Chrome', 'HKCU:\Software\Chromium', 'HKCU:\Software\Microsoft\Edge') {
        $key = "$base\NativeMessagingHosts\$HostName"
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    }

    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }

    Write-Host 'VidGrab удалён.' -ForegroundColor Green
    Write-Host 'Само расширение убери на странице расширений браузера (кнопка «Удалить» у VidGrab).'
    exit 0
}
catch {
    Write-Host "Ошибка: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
