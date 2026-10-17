$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectDir ('test-data\ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$fixtureExe = Join-Path $projectDir 'bin\WindowHome.Fixture.exe'
$children = @()
foreach ($name in @('Alpha','Beta')) {
    $fixtureDir = Join-Path $testDir $name
    New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
    $fixtureCopy = Join-Path $testDir ($name + '.exe')
    Copy-Item -LiteralPath $fixtureExe -Destination $fixtureCopy
    $child = Start-Process -FilePath $fixtureCopy -ArgumentList @(('"' + $fixtureDir + '"'),$name) -PassThru
    $children += $child.Id
}
$uiData = Join-Path $testDir 'profiles'
New-Item -ItemType Directory -Force -Path $uiData | Out-Null
$reportXml = for ($i = 0; $i -lt 105; $i++) {
    $kind = $i % 3
    $trigger = @('一键恢复','系统唤醒','启动软件')[$kind]
    $firstSuccess = if ($kind -eq 2) { 'false' } else { 'true' }
    $secondSuccess = if ($kind -eq 0) { 'true' } else { 'false' }
    $at = [DateTime]::UtcNow.AddMinutes(-$i).ToString('o')
    '<RestoreReport><At>' + $at + '</At><LayoutName>测试方案</LayoutName><Trigger>' + $trigger + '</Trigger><Items><RestoreItem><App>Alpha</App><Title>资料</Title><Success>' + $firstSuccess + '</Success></RestoreItem><RestoreItem><App>Beta</App><Title>编辑</Title><Success>' + $secondSuccess + '</Success></RestoreItem></Items></RestoreReport>'
}
[System.IO.File]::WriteAllText((Join-Path $uiData 'state.xml'), ('<AppData><Version>1</Version><Options><Paused>true</Paused><RestoreHotkey></RestoreHotkey><SaveHotkey></SaveHotkey></Options><Reports>' + ($reportXml -join '') + '</Reports></AppData>'))
$appExe = Join-Path $projectDir 'bin\WindowHome.exe'
$ui = Start-Process -FilePath $appExe -ArgumentList @('--data-dir',('"' + $uiData + '"'),'--test-processes',($children -join ',')) -PassThru
[pscustomobject]@{ UiProcess = $ui.Id; FixtureProcesses = $children; TestDirectory = $testDir } | ConvertTo-Json
