param([string]$Browser = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(!(Test-Path -LiteralPath $Browser)){throw 'A Chromium browser is required; specify -Browser with its executable path.'}
$outputRoot=Join-Path $projectRoot 'artifacts-design'
$fixtures=@{}
foreach($name in @('success','failed','canceled','views','unchanged','edits','placement','constraints')){
    $file=if($name -eq 'success'){'viewer-demo.json'}else{"viewer-$name.json"}
    $fixtures[$name]=[IO.File]::ReadAllText((Join-Path $outputRoot $file))
}
$fixtureJson=($fixtures|ConvertTo-Json -Compress).Replace('<','\u003c')
$checks=[IO.File]::ReadAllText((Join-Path $projectRoot 'tests\BlockAI.ViewerChecks\checks.js'))
$checks=$checks.Replace('</script','<\/script')
$html=[IO.File]::ReadAllText((Join-Path $outputRoot 'viewer-demo.html'))
$html=$html.Replace('</body>',"<script>const fixtures=$fixtureJson;`n$checks</script></body>")
$harness=Join-Path $outputRoot 'viewer-browser-checks.html'
[IO.File]::WriteAllText($harness,$html)
foreach($width in @(1600,720,960)){
    if($width -eq 960){
        # Also open the source viewer itself, with no generated recording embedded.
        $empty=[IO.File]::ReadAllText((Join-Path $projectRoot 'src\BlockAI.Core\DesignViewer.html'))
        $smoke='<script>const report=document.createElement("pre");report.id="viewer-test-results";const ok=Boolean(window.blockAIViewer)&&document.getElementById("error").hidden&&!document.getElementById("empty").hidden;report.dataset.passed=String(ok);report.textContent=ok?"VIEWER_CHECKS_PASSED 1":"VIEWER_CHECKS_FAILED empty viewer";document.body.append(report);</script>'
        $harness=Join-Path $outputRoot 'viewer-empty-checks.html'
        [IO.File]::WriteAllText($harness,$empty.Replace('</body>',$smoke+'</body>'))
    }
    $profile=Join-Path $projectRoot ".tools\viewer-checks-$width-$PID"
    $stdout=Join-Path $outputRoot "viewer-browser-$width.html"
    $stderr=Join-Path $outputRoot "viewer-browser-$width.log"
    $screenshot=Join-Path $outputRoot "viewer-browser-$width.png"
    $arguments=@('--headless','--disable-gpu','--disable-background-timer-throttling','--no-first-run','--no-default-browser-check',"--user-data-dir=`"$profile`"",'--allow-file-access-from-files','--dump-dom',"--screenshot=`"$screenshot`"","--window-size=$width,1200",'--virtual-time-budget=15000',"`"$(([Uri]$harness).AbsoluteUri)`"")
    $browserProcess=Start-Process -FilePath $Browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if(!$browserProcess.WaitForExit(45000)){Stop-Process -Id $browserProcess.Id;throw 'Browser checks timed out.'}
    # Edge's launcher can exit before its headless child finishes writing redirected output.
    $dom=''
    for($poll=0;$poll -lt 80;$poll++){
        if(Test-Path -LiteralPath $stdout){
            $stream=[IO.File]::Open($stdout,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $reader=New-Object IO.StreamReader($stream)
            try{$dom=$reader.ReadToEnd()}finally{$reader.Dispose()}
            if($dom -match 'data-passed="(?:true|false)"'){break}
        }
        Start-Sleep -Milliseconds 500
    }
    if($dom -notmatch 'data-passed="true"'){
        $match=[regex]::Match($dom,'(?s)<pre id="viewer-test-results".*?</pre>')
        throw "Viewer checks failed at width $width. $($match.Value) See $stderr"
    }
    $match=[regex]::Match($dom,'VIEWER_CHECKS_PASSED \d+')
    Write-Host "$width px: $($match.Value). Screenshot: $screenshot"
}
