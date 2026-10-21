param([Parameter(Mandatory=$true)][string[]]$Files,[string]$AutoCADDir='C:\Program Files\Autodesk\AutoCAD 2026')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$outputRoot=Join-Path $projectRoot 'artifacts-references'
$copies=Join-Path $outputRoot 'source-copies'
New-Item -ItemType Directory -Force $copies | Out-Null
$dll=Join-Path $projectRoot 'tools\BlockAI.ReferenceExport\bin\Release\net8.0-windows\BlockAI.ReferenceExport.dll'
if(!(Test-Path -LiteralPath $dll)){throw 'Build tools/BlockAI.ReferenceExport first.'}
$env:BLOCKAI_REFERENCE_OUTPUT=$outputRoot
$runtime=Join-Path ([IO.Path]::GetTempPath()) 'BlockAI-reference-tools'
New-Item -ItemType Directory -Force $runtime | Out-Null
Copy-Item -LiteralPath $dll -Destination $runtime
$dll=Join-Path $runtime 'BlockAI.ReferenceExport.dll'
$dllDirectory=(Split-Path $dll -Parent).Replace('\','/')
$scriptPath=Join-Path $outputRoot 'export.scr'
$script=@"
(setq blockaiOldTrust (getvar "TRUSTEDPATHS"))
(if (and (wcmatch blockaiOldTrust ";*/tools/BlockAI.ReferenceExport/bin/*") (not (vl-string-search ";" blockaiOldTrust 1))) (setq blockaiOldTrust ""))
(setvar "TRUSTEDPATHS" (strcat blockaiOldTrust ";$dllDirectory"))
NETLOAD
"$($dll.Replace('\','/'))"
AIREFEXPORT
(setvar "TRUSTEDPATHS" blockaiOldTrust)
QUIT
_Y

"@
[IO.File]::WriteAllText($scriptPath,$script)
foreach($file in $Files){
    $resolved=(Resolve-Path -LiteralPath $file).Path
    $copy=Join-Path $copies (Split-Path $resolved -Leaf)
    Copy-Item -LiteralPath $resolved -Destination $copy
    $stem=[IO.Path]::GetFileNameWithoutExtension($copy)
    $arguments=@('/i',"`"$copy`"",'/s',"`"$scriptPath`"",'/l','en-US')
    $process=Start-Process -FilePath (Join-Path $AutoCADDir 'accoreconsole.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputRoot "$stem-console.log") -RedirectStandardError (Join-Path $outputRoot "$stem-error.log")
    if(!$process.WaitForExit(55000)){Stop-Process -Id $process.Id;throw "Reference inspection timed out: $stem"}
    $summary=Join-Path $outputRoot "$stem-summary.json"
    if(!(Test-Path -LiteralPath $summary)){throw "Reference inspection did not produce a summary. See artifacts-references/$stem-console.log"}
    Write-Host ([IO.File]::ReadAllText($summary))
}
