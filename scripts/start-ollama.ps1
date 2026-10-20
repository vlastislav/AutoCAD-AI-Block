param([string]$InstallRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$env:OLLAMA_MODELS = Join-Path $InstallRoot 'models'
$env:OLLAMA_HOST = '127.0.0.1:11434'
$env:OLLAMA_NO_CLOUD = '1'
$env:OLLAMA_NUM_PARALLEL = '1'
$env:OLLAMA_CONTEXT_LENGTH = '16384'
$executable = Join-Path $InstallRoot 'app\ollama.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw "Ollama is not installed at $executable" }
try {
    $null = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 3
    Write-Host 'Ollama is already running.'
    return
} catch { }
$logDirectory = Join-Path $InstallRoot 'logs'
New-Item -ItemType Directory -Force $logDirectory | Out-Null
Start-Process -FilePath $executable -ArgumentList 'serve' -WorkingDirectory $InstallRoot -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logDirectory 'server-out.log') `
    -RedirectStandardError (Join-Path $logDirectory 'server-error.log') | Out-Null
Write-Host 'Ollama started in the background on 127.0.0.1:11434.'
