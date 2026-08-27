param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.]*$')]
    [string]$ProjectName,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [string]$SourceProjectName = "MyProject",

    [string]$UserSecretsId = [guid]::NewGuid().ToString(),

    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)

if ((Test-Path -LiteralPath $destinationFullPath) -and -not $Force) {
    throw "Destination already exists. Use -Force to overwrite: $destinationFullPath"
}

if (Test-Path -LiteralPath $destinationFullPath) {
    Remove-Item -LiteralPath $destinationFullPath -Recurse -Force
}

# 這些目錄可能出現在任何層級（例如 src/MyProject/MyProject.Web/bin），必須遞迴排除。
$excludedDirectories = @(".git", "bin", "obj", ".vs", ".playwright-cli", "output")
$excludedFilePatterns = @("*.user", "*.suo")

function Copy-TreeExcluding {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory
    )

    if (-not (Test-Path -LiteralPath $TargetDirectory)) {
        New-Item -ItemType Directory -Path $TargetDirectory | Out-Null
    }

    foreach ($item in Get-ChildItem -LiteralPath $SourceDirectory -Force) {
        if ($item.PSIsContainer) {
            if ($excludedDirectories -contains $item.Name) {
                continue
            }

            Copy-TreeExcluding -SourceDirectory $item.FullName -TargetDirectory (Join-Path $TargetDirectory $item.Name)
            continue
        }

        $isExcludedFile = $false
        foreach ($pattern in $excludedFilePatterns) {
            if ($item.Name -like $pattern) {
                $isExcludedFile = $true
                break
            }
        }

        if ($isExcludedFile) {
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $TargetDirectory $item.Name) -Force
    }
}

Copy-TreeExcluding -SourceDirectory $repoRoot -TargetDirectory $destinationFullPath

$textExtensions = @(
    ".cs", ".csproj", ".slnx", ".json", ".md", ".razor", ".css", ".js",
    ".ps1", ".yml", ".yaml", ".config", ".xml"
)

function Test-Utf8Bom {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $head = New-Object byte[] 3
        $read = $stream.Read($head, 0, 3)
        return ($read -eq 3 -and $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF)
    }
    finally {
        $stream.Dispose()
    }
}

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    ForEach-Object {
        # 逐檔保留原本的 BOM 狀態：docs/**/*.md 必須維持 UTF-8 含 BOM，
        # 否則複製出來的專案會直接卡在 scripts/Test-DocsEncoding.ps1。
        # 注意：Set-Content -Encoding utf8 在 PowerShell 7 是「不含 BOM」。
        $hasBom = Test-Utf8Bom -Path $_.FullName
        $content = [System.IO.File]::ReadAllText($_.FullName)
        $content = $content.Replace($SourceProjectName, $ProjectName)
        if ($_.Name -eq "appsettings.json") {
            $content = $content.Replace("DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars", "$ProjectName-ChangeThisJwtSigningKey-AtLeast32Chars")
            $content = $content.Replace('"SupportPassword": "support"', '"SupportPassword": "change-me"')
        }
        [System.IO.File]::WriteAllText($_.FullName, $content, (New-Object System.Text.UTF8Encoding($hasBom)))
    }

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -Directory |
    Sort-Object FullName -Descending |
    Where-Object { $_.Name.Contains($SourceProjectName) } |
    ForEach-Object {
        $newName = $_.Name.Replace($SourceProjectName, $ProjectName)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $_.Name.Contains($SourceProjectName) } |
    ForEach-Object {
        $newName = $_.Name.Replace($SourceProjectName, $ProjectName)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

# 每個衍生專案都必須擁有自己的 UserSecretsId，否則會共用同一份 secrets.json 互相污染。
$sourceUserSecretsId = "83f6d54f-9f34-4cd9-a626-d4c05c996e5d"
$webCsproj = Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File -Filter "$ProjectName.Web.csproj" |
    Select-Object -First 1

if ($webCsproj) {
    $hasBom = Test-Utf8Bom -Path $webCsproj.FullName
    $content = [System.IO.File]::ReadAllText($webCsproj.FullName)
    $content = [regex]::Replace($content, '<UserSecretsId>[^<]*</UserSecretsId>', "<UserSecretsId>$UserSecretsId</UserSecretsId>")
    [System.IO.File]::WriteAllText($webCsproj.FullName, $content, (New-Object System.Text.UTF8Encoding($hasBom)))
    Write-Host "UserSecretsId set to $UserSecretsId in $($webCsproj.Name)"
}
else {
    Write-Warning "Could not locate $ProjectName.Web.csproj; UserSecretsId was not replaced."
}

$remainingMatches = Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    Select-String -Pattern $SourceProjectName, "DevelopmentOnly-ChangeThisJwtSigningKey", '"SupportPassword": "support"', $sourceUserSecretsId -SimpleMatch

if ($remainingMatches) {
    Write-Warning "Scaffold completed, but safety checks found values that still need review:"
    $remainingMatches | ForEach-Object {
        Write-Warning "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
    }
}

Write-Host "Created starter project at $destinationFullPath"
Write-Host "Next: see docs/guides/VS Code 開發環境與新專案上手指南.md - section 7 (branding: favicon, brand image, product name/description) and sections 8.1 / 8.3 for the manual follow-up items."
