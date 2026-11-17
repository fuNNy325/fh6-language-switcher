param([switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 before building.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Runtime.Serialization.dll', '/r:System.IO.Compression.dll')
$app = Join-Path $output 'FH6LanguageSwitcher.exe'
& $compiler /nologo /optimize+ /warnaserror+ /target:winexe /platform:anycpu /codepage:65001 "/out:$app" "/win32manifest:$PSScriptRoot\src\app.manifest" @references /r:System.Drawing.dll /r:System.Windows.Forms.dll "$PSScriptRoot\src\Program.cs" "$PSScriptRoot\src\MainForm.cs" "$PSScriptRoot\src\SwapEngine.cs"
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath "$PSScriptRoot\src\FH6LanguageSwitcher.exe.config" -Destination $output -Force
Write-Output "Built: $app"
if ($Test) {
    $testExe = Join-Path $output 'EngineTests.exe'
    & $compiler /nologo /optimize+ /warnaserror+ /target:exe /codepage:65001 "/out:$testExe" @references "$PSScriptRoot\src\SwapEngine.cs" "$PSScriptRoot\tests\EngineTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Engine tests failed.' }
}
