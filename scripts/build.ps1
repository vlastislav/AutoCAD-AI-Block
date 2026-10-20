param(
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2026',
    [string]$OutputDirectory = 'artifacts'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetCommand build "$projectRoot\src\BlockAI.AutoCAD\BlockAI.AutoCAD.csproj" -c Release "-p:AutoCADDir=$AutoCADDir"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
& $dotnetCommand run --project "$projectRoot\tests\BlockAI.Checks\BlockAI.Checks.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
$outputRoot = Join-Path $projectRoot $OutputDirectory
$destination = Join-Path $outputRoot 'BlockAI.bundle\Contents\Windows'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -LiteralPath "$projectRoot\src\BlockAI.AutoCAD\bin\Release\net8.0-windows\BlockAI.dll" -Destination $destination
Copy-Item -LiteralPath "$projectRoot\src\BlockAI.AutoCAD\bin\Release\net8.0-windows\BlockAI.Core.dll" -Destination $destination
Copy-Item -LiteralPath "$projectRoot\packaging\PackageContents.xml" -Destination "$outputRoot\BlockAI.bundle"
Write-Host "Ready: $outputRoot\BlockAI.bundle"
