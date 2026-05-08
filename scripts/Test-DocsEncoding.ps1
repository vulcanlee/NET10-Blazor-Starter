param(
    [string]$DocsPath = "docs"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path $DocsPath
$failed = $false

Get-ChildItem -Path $root -Filter "*.md" -File | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $hasReplacementCharacter = $text.Contains([char]0xFFFD)

    if (-not $hasBom -or $hasReplacementCharacter) {
        $failed = $true
        Write-Host "FAILED $($_.FullName): BOM=$hasBom ReplacementCharacter=$hasReplacementCharacter"
    }
    else {
        Write-Host "OK $($_.FullName): BOM=$hasBom ReplacementCharacter=$hasReplacementCharacter"
    }
}

if ($failed) {
    throw "Documentation encoding check failed."
}
