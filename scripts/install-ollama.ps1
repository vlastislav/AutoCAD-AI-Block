param([string]$InstallRoot = 'D:\Ollama')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not [IO.Path]::IsPathRooted($InstallRoot)) { throw 'InstallRoot must be an absolute path.' }
$installPath = [IO.Path]::GetFullPath($InstallRoot)
foreach ($directory in @('downloads', 'app', 'models', 'logs', 'config')) {
    New-Item -ItemType Directory -Force (Join-Path $installPath $directory) | Out-Null
}
$release = Invoke-RestMethod 'https://api.github.com/repos/ollama/ollama/releases/latest'
$asset = $release.assets | Where-Object name -eq 'ollama-windows-amd64.zip'
$checksums = $release.assets | Where-Object name -eq 'sha256sum.txt'
if (-not $asset -or -not $checksums) { throw 'Official release is missing ZIP or checksums.' }
$archive = Join-Path $installPath 'downloads\ollama-windows-amd64.zip'
Write-Host "Downloading official Ollama $($release.tag_name) ($([Math]::Round($asset.size / 1GB, 2)) GiB) to $archive"
if (-not (Test-Path -LiteralPath $archive) -or (Get-Item -LiteralPath $archive).Length -ne $asset.size) {
    & curl.exe --fail --location --retry 3 --output $archive $asset.browser_download_url
    if ($LASTEXITCODE -ne 0) { throw 'Ollama download failed.' }
}
$checksumText = (Invoke-WebRequest $checksums.browser_download_url -UseBasicParsing).Content
if ($checksumText -is [byte[]]) { $checksumText = [Text.Encoding]::UTF8.GetString($checksumText) }
$checksumLine = $checksumText -split "`n" | Where-Object { $_ -match '\s+\*?(\./)?ollama-windows-amd64\.zip\s*$' }
if (@($checksumLine).Count -ne 1) { throw 'No unique official checksum found.' }
$expected = ($checksumLine.Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Ollama ZIP checksum mismatch. Archive not extracted.' }
Write-Host 'SHA256 verified. Extracting portable application...'
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $installPath 'app') -Force
# Ollama stores a small configuration/key directory in the Windows profile.
# On a fresh installation, use a junction so the actual data lives on D: too.
$profileConfig = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.ollama'
if (-not (Test-Path -LiteralPath $profileConfig)) {
    New-Item -ItemType Junction -Path $profileConfig -Target (Join-Path $installPath 'config') | Out-Null
} else {
    Write-Host "Existing configuration at $profileConfig preserved; no files were moved."
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-ollama.ps1') -Destination (Join-Path $installPath 'start-ollama.ps1')
Set-Content -LiteralPath (Join-Path $installPath 'installed-version.txt') -Value "$($release.tag_name)`nSHA256 $actual" -Encoding UTF8
Write-Host "Installed at $installPath. Start with powershell -ExecutionPolicy Bypass -File $installPath\start-ollama.ps1"
