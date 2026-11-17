$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$demo = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\demo\media\Stripped\StringTables'
New-Item -ItemType Directory -Path $demo -Force | Out-Null
foreach ($code in @('CHS', 'EN', 'JP', 'DE')) {
    $zipPath = Join-Path $demo ($code + '.zip')
    if (Test-Path -LiteralPath $zipPath) { throw "Demo already exists: $zipPath. Reuse it instead of overwriting." }
    $file = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($file, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $writer = New-Object System.IO.StreamWriter($zip.CreateEntry('demo.str').Open())
            try { $writer.Write("Synthetic test data: $code") } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $file.Dispose() }
}
Write-Output $demo
