param([switch]$Test, [switch]$BuildTests, [switch]$RenderUi, [string]$OutputDirectory = 'bin')
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDir 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
$releaseDir = Join-Path $projectDir $OutputDirectory
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$iconPath = Join-Path $projectDir 'assets\WindowHome.ico'
if (-not (Test-Path -LiteralPath $iconPath)) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap 64,64
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.Clear([System.Drawing.Color]::FromArgb(17,30,49))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(89,215,197)),4
    $graphics.DrawRectangle($pen,10,10,44,44)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(89,215,197))
    $graphics.FillRectangle($brush,18,18,16,16)
    $graphics.FillRectangle($brush,39,28,7,18)
    $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    $stream = [System.IO.File]::Create($iconPath)
    $icon.Save($stream)
    $stream.Dispose(); $icon.Dispose(); $brush.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkDir $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $frameworkDir 'WPF') $_) }
$sources = Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$exePath = Join-Path $releaseDir 'WindowHome.exe'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /langversion:5 /codepage:65001 "/out:$exePath" "/win32manifest:$(Join-Path $projectDir 'src\app.manifest')" "/win32icon:$iconPath" "/resource:$(Join-Path $projectDir 'src\Main.xaml'),WindowHome.Main.xaml" $references $sources
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectDir 'src\App.config') -Destination ($exePath + '.config') -Force
Write-Output "Built: $exePath"
if ($Test -or $BuildTests) {
    $testSource = Join-Path $projectDir 'tests\Runner.cs'
    & $compilerPath /nologo /target:exe /platform:x64 /optimize+ /langversion:5 /codepage:65001 "/out:$(Join-Path $releaseDir 'WindowHome.Tests.exe')" "/reference:$exePath" $references $testSource
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
    $fixturePath = Join-Path $releaseDir 'WindowHome.Fixture.exe'
    $fixtureSource = Join-Path $projectDir 'tests\Fixture.cs'
    if (-not (Test-Path -LiteralPath $fixturePath) -or (Get-Item -LiteralPath $fixtureSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $fixturePath).LastWriteTimeUtc) {
        & $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 /codepage:65001 "/out:$fixturePath" "/win32manifest:$(Join-Path $projectDir 'src\app.manifest')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $fixtureSource
        if ($LASTEXITCODE -ne 0) { throw '测试窗口编译失败。' }
    }
    if ($Test) {
        & (Join-Path $releaseDir 'WindowHome.Tests.exe')
        if ($LASTEXITCODE -ne 0) { throw '测试未通过。' }
    }
}
if ($RenderUi) {
    $renderPath = Join-Path $releaseDir 'WindowHome.RenderUi.exe'
    & $compilerPath /nologo /target:exe /platform:x64 /optimize+ /langversion:5 /codepage:65001 "/out:$renderPath" "/reference:$exePath" $references (Join-Path $projectDir 'tests\RenderUi.cs')
    if ($LASTEXITCODE -ne 0) { throw '界面渲染器编译失败。' }
    & $renderPath
    if ($LASTEXITCODE -ne 0) { throw '界面布局检查未通过。' }
}

