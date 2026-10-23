& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo = 'Chifer123536/vidgrab'
    $work = Join-Path $env:TEMP ('vidgrab-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        $zip = Join-Path $work 'src.zip'
        Write-Host "Downloading $repo ..."
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/$repo/archive/refs/heads/main.zip" -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $work -Force
        $src = Get-ChildItem $work -Directory | Select-Object -First 1
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src.FullName 'install.ps1')
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
