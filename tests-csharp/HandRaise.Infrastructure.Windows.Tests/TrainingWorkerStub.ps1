param([string]$Mode = 'Complete')
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'Child') { while ($true) { Start-Sleep -Milliseconds 100 } }
$null = [Console]::ReadLine()
if ($Mode -eq 'Crash') { exit 13 }
if ($Mode -eq 'Malformed') { [Console]::WriteLine('invalid-json'); exit 0 }
if ($Mode -eq 'ChildCrash') {
    $executable = Join-Path $PSHOME 'powershell.exe'
    $child = Start-Process -FilePath $executable -WindowStyle Hidden -ArgumentList @('-NoProfile', '-NonInteractive', '-File', ('"' + $PSCommandPath + '"'), '-Mode', 'Child') -PassThru
    [IO.File]::WriteAllText((Join-Path (Get-Location) 'child.pid'), [string]$child.Id)
    exit 13
}
[Console]::WriteLine('{"protocol_version":1,"type":"progress","stage":"training","percent":25,"current_epoch":1,"total_epochs":4}')
if ($Mode -eq 'Cancel') {
    $command = [Console]::ReadLine() | ConvertFrom-Json
    if ($command.type -eq 'cancel') { [IO.File]::WriteAllText((Join-Path (Get-Location) 'cancelled'), 'yes') }
    exit 2
}
if ($Mode -eq 'IgnoreCancel') { while ($true) { Start-Sleep -Milliseconds 100 } }
[Console]::WriteLine('{"protocol_version":1,"type":"progress","stage":"validating","percent":80}')
[Console]::WriteLine('{"protocol_version":1,"type":"progress","stage":"exporting","percent":90}')
[Console]::WriteLine('{"protocol_version":1,"type":"completed","model_path":"model.onnx","worker_version":"stub-1","device":"cpu","metrics":{"map50":0.8}}')
exit 0
