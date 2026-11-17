$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$package = Join-Path $dist 'FH6LanguageSwitcher-v1.0.0-win.zip'
$files = @(
    (Join-Path $dist 'FH6LanguageSwitcher.exe'),
    (Join-Path $dist 'FH6LanguageSwitcher.exe.config'),
    (Join-Path $root 'README.md'),
    (Join-Path $root 'LICENSE')
)
foreach ($file in $files) { if (-not (Test-Path -LiteralPath $file)) { throw "Missing: $file. Run build.ps1 first." } }
Compress-Archive -LiteralPath $files -DestinationPath $package -Force
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($package + '.sha256', $hash + '  ' + [System.IO.Path]::GetFileName($package) + "`n", (New-Object System.Text.UTF8Encoding($false)))
Get-Item -LiteralPath $package, ($package + '.sha256') | Select-Object Name, Length

# Explicit allowlist: source releases must never contain local game files or backups.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourcePackage = Join-Path $dist 'FH6LanguageSwitcher-v1.0.0-source.zip'
$sourceFiles = @(
    '.gitignore', '.gitattributes', 'LICENSE', 'README.md', 'CONTRIBUTING.md',
    'build.ps1', '.github\workflows\build.yml',
    'src\SwapEngine.cs', 'src\MainForm.cs', 'src\Program.cs',
    'src\app.manifest', 'src\FH6LanguageSwitcher.exe.config',
    'tests\EngineTests.cs', 'scripts\New-Demo.ps1', 'scripts\Package.ps1'
)
$sourceStream = [System.IO.File]::Open($sourcePackage, [System.IO.FileMode]::Create)
try {
    $sourceZip = New-Object System.IO.Compression.ZipArchive($sourceStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $sourceFiles) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceZip, (Join-Path $root $relative), $relative.Replace('\', '/')) | Out-Null
        }
    } finally { $sourceZip.Dispose() }
} finally { $sourceStream.Dispose() }
Get-Item -LiteralPath $sourcePackage | Select-Object Name, Length
