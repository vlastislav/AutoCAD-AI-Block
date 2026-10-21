param(
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2026',
    [string]$OutputDirectory = 'artifacts',
    [switch]$CheckViewer
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
& $dotnetCommand run --project "$projectRoot\tests\BlockAI.DesignChecks\BlockAI.DesignChecks.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Design loop checks failed.' }
& $dotnetCommand run --project "$projectRoot\tests\BlockAI.ReferenceChecks\BlockAI.ReferenceChecks.csproj" -c Release
if($LASTEXITCODE -ne 0){throw 'Reference checks failed.'}
if($CheckViewer){
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$projectRoot\scripts\check-viewer.ps1"
    if($LASTEXITCODE -ne 0){throw 'Viewer browser checks failed.'}
}
$outputRoot = Join-Path $projectRoot $OutputDirectory
$destination = Join-Path $outputRoot 'BlockAI.bundle\Contents\Windows'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -LiteralPath "$projectRoot\src\BlockAI.AutoCAD\bin\Release\net8.0-windows\BlockAI.dll" -Destination $destination
Copy-Item -LiteralPath "$projectRoot\src\BlockAI.AutoCAD\bin\Release\net8.0-windows\BlockAI.Core.dll" -Destination $destination
Copy-Item -LiteralPath "$projectRoot\packaging\PackageContents.xml" -Destination "$outputRoot\BlockAI.bundle"
Copy-Item -LiteralPath "$projectRoot\artifacts-design\viewer-demo.html" -Destination "$outputRoot\design-viewer.html"
Copy-Item -LiteralPath "$projectRoot\artifacts-design\viewer-demo.json" -Destination "$outputRoot\design-viewer-demo.json"
Copy-Item -LiteralPath "$projectRoot\artifacts-design\viewer-views.html" -Destination "$outputRoot\three-view-demo.html"
Copy-Item -LiteralPath "$projectRoot\artifacts-design\viewer-views.json" -Destination "$outputRoot\three-view-demo.json"
Copy-Item -LiteralPath "$projectRoot\artifacts-design\shared-table-views.png" -Destination "$outputRoot\three-view-demo.png"
Write-Host "Ready: $outputRoot\BlockAI.bundle"
