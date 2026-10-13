[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$destination = Join-Path $PSScriptRoot 'tools\hdiffpatch'
$toolHashes = @{
    'hdiffz.exe' = 'f5ed7ac622a2daf4a31cc21ffa8ea1717f92323e79ef5ae695c5c9238a282f52'
    'hpatchz.exe' = '9703c694b5955c576d9f0e26e98b60941f0bbb53b382b1f1988d75d461e580cb'
}
$missing = @()
foreach ($name in $toolHashes.Keys) {
    $path = Join-Path $destination $name
    if (-not (Test-Path -LiteralPath $path)) { $missing += $name; continue }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $toolHashes[$name]) {
        throw "Existing tool SHA-256 mismatch; refusing overwrite: $path"
    }
}
if ($missing.Count -eq 0) { Write-Output 'HDiffPatch v5.1.3 tools already installed and verified.'; return }
$packageHash = '77f141386e5d8f785c1c846e10fbbc19b6c05aa00e3f59cc44670fb3f0e2ae94'
$url = 'https://github.com/sisong/HDiffPatch/releases/download/v5.1.3/hdiffpatch_v5.1.3_bin_windows64.zip'
$archivePath = Join-Path ([IO.Path]::GetTempPath()) ('GamePatchDepot-hdiff-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri $url -OutFile $archivePath
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $packageHash) {
        throw 'HDiffPatch package SHA-256 mismatch.'
    }
    $null = New-Item -ItemType Directory -Path $destination -Force
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($name in $missing) {
            $entries = @($archive.Entries | Where-Object { $_.Name -eq $name })
            if ($entries.Count -ne 1) { throw "Missing or ambiguous tool: $name" }
            $output = Join-Path $destination $name
            if (Test-Path -LiteralPath $output) { throw "Tool already exists; do not overwrite: $output" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $output, $false)
            if ((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant() -ne $toolHashes[$name]) {
                throw "Extracted tool SHA-256 mismatch: $output"
            }
        }
    }
    finally { $archive.Dispose() }
    Write-Output 'Installed HDiffPatch v5.1.3 Windows x64 from the pinned, SHA-256-verified official package.'
}
finally {
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
}
