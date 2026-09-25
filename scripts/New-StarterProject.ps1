param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.]*$')]
    [string]$ProjectName,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [string]$SourceProjectName = "MyProject",

    [string]$UserSecretsId = [guid]::NewGuid().ToString(),

    [switch]$Force,

    # 預設會清掉腳手架自己的開發史（docs/changelog 內容、docs/planning、docs/superpowers），
    # 那些對衍生專案沒有用處。要原封不動整份複製時加這個開關。
    [switch]$KeepStarterHistory
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)

# 衍生專案不帶腳手架的 migration 歷史，最後會清空 Migrations 並以 dotnet ef 重建單一 Init。
# 先檢查 dotnet-ef 是否可用，避免複製到一半才失敗、留下半成品。
$dotnetEfAvailable = $false
try {
    & dotnet ef --version *> $null
    $dotnetEfAvailable = ($LASTEXITCODE -eq 0)
}
catch {
    $dotnetEfAvailable = $false
}

if (-not $dotnetEfAvailable) {
    throw "dotnet-ef is required to create the initial migration. Install it with: dotnet tool install --global dotnet-ef"
}

# 每個衍生專案都要有自己的開發連接埠，否則同一台機器同時開兩個專案會搶埠；
# Google OAuth 的 redirect URI 也是依埠註冊。範圍比照 VS／dotnet new（http 5000–5300、https 7000–7300）。
$sourceHttpPort = 5109
$sourceHttpsPort = 7144

function Get-FreeDevPort {
    param(
        [Parameter(Mandatory = $true)][int]$Minimum,
        [Parameter(Mandatory = $true)][int]$Maximum,
        [Parameter(Mandatory = $true)][int]$Exclude
    )

    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $candidate = Get-Random -Minimum $Minimum -Maximum ($Maximum + 1)
        if ($candidate -eq $Exclude) {
            continue
        }

        # 用實際試綁判斷：同時涵蓋「已被占用」與 Windows（Hyper-V）保留的排除埠範圍。
        $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $candidate)
        try {
            $listener.Start()
            return $candidate
        }
        catch {
            continue
        }
        finally {
            $listener.Stop()
        }
    }

    throw "Could not find a free port between $Minimum and $Maximum."
}

$httpPort = Get-FreeDevPort -Minimum 5000 -Maximum 5300 -Exclude $sourceHttpPort
$httpsPort = Get-FreeDevPort -Minimum 7000 -Maximum 7300 -Exclude $sourceHttpsPort

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

# 每個衍生專案都必須擁有自己的 UserSecretsId，否則會共用同一份 secrets.json 互相污染。
# 除了 csproj，文件裡的路徑範例也要一起換掉，否則會把開發者導回原腳手架的 secrets 目錄。
$sourceUserSecretsId = "83f6d54f-9f33-4cd9-a626-d4c05c996e5d"

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

function Test-StarterHistoryLink {
    param(
        [Parameter(Mandatory = $true)][string]$FileDirectory,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$DocsRoot,
        [Parameter(Mandatory = $true)][string[]]$HistoryDirectories
    )

    # 只處理指向被清理目錄、而且真的已經不存在的連結；
    # 其他死連結（若原本就有）不在本次職責範圍，誤改反而難追。
    if ($Target -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
        return $false
    }

    $relativePath = $Target.Split('#')[0]
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $false
    }

    $relativePath = [uri]::UnescapeDataString($relativePath)
    try {
        $full = [System.IO.Path]::GetFullPath((Join-Path $FileDirectory $relativePath))
    }
    catch {
        return $false
    }

    $inHistory = $false
    foreach ($name in $HistoryDirectories) {
        $historyPath = [System.IO.Path]::GetFullPath((Join-Path $DocsRoot $name))
        if ($full -eq $historyPath -or $full.StartsWith($historyPath + [System.IO.Path]::DirectorySeparatorChar)) {
            $inHistory = $true
            break
        }
    }

    if (-not $inHistory) {
        return $false
    }

    return -not (Test-Path -LiteralPath $full)
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
        $content = $content.Replace($sourceUserSecretsId, $UserSecretsId)
        # launchSettings.json 與文件裡的網址一起換；只換帶 localhost: 的形式，避免誤傷其他數字。
        $content = $content.Replace("localhost:$sourceHttpsPort", "localhost:$httpsPort")
        $content = $content.Replace("localhost:$sourceHttpPort", "localhost:$httpPort")
        if ($_.Name -eq "appsettings.json") {
            # 註：這裡刻意**不**動 BootstrapSettings:SupportPassword。
            # 換成另一個固定佔位值並不會比較安全，只是把弱值換成另一個弱值；
            # 真正的防線是 StartupSafetyValidator —— 本機開發仍可用文件記載的預設帳密登入，
            # Production 則會因為「仍是範本預設密碼」被擋下啟動。
            $content = $content.Replace("DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars", "$ProjectName-ChangeThisJwtSigningKey-AtLeast32Chars")
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

# csproj 是 UserSecretsId 的權威來源：即使有人改過腳手架的預設值（上面的字串取代因此沒命中），
# 這段 regex 也一定會把新的 Id 寫進去。
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

# 清空腳手架的 migration 歷史，改由新專案自己的第一次 migration（Init）起算。
# 名稱用大寫 Init：全小寫類別名會觸發 CS8981，在 TreatWarningsAsErrors 下直接建置失敗。
# 目錄本身保留：AccessDatas.csproj 有 <Folder Include="Migrations\" />。
$projectRoot = Join-Path $destinationFullPath "src/$ProjectName"
$accessDatasCsproj = Join-Path $projectRoot "$ProjectName.AccessDatas/$ProjectName.AccessDatas.csproj"
$webCsprojPath = Join-Path $projectRoot "$ProjectName.Web/$ProjectName.Web.csproj"
$migrationsDirectory = Join-Path $projectRoot "$ProjectName.AccessDatas/Migrations"

if (Test-Path -LiteralPath $migrationsDirectory) {
    Get-ChildItem -LiteralPath $migrationsDirectory -Force | Remove-Item -Recurse -Force
}

# 這支測試寫死了腳手架的舊 migration 名稱（驗證腳手架自己的升級路徑），重建後在新專案必定失效。
$starterMigrationTest = Join-Path $projectRoot "$ProjectName.Tests/CategoryTeamUniqueIndexMigrationTests.cs"
if (Test-Path -LiteralPath $starterMigrationTest) {
    Remove-Item -LiteralPath $starterMigrationTest -Force
}

# dotnet ef 取專案中繼資料前需要 project.assets.json，全新複製的專案必須先還原套件。
$solutionFile = Join-Path $projectRoot "$ProjectName.slnx"
& dotnet restore $solutionFile
if ($LASTEXITCODE -ne 0) {
    throw "Failed to restore packages. Fix the error above, then run manually: dotnet restore `"$solutionFile`""
}

$migrationCommand = "dotnet ef migrations add Init --project `"$accessDatasCsproj`" --startup-project `"$webCsprojPath`""
Write-Host "Creating initial migration: $migrationCommand"
& dotnet ef migrations add Init --project $accessDatasCsproj --startup-project $webCsprojPath
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create the initial migration. Fix the error above, then run manually: $migrationCommand"
}

Write-Host "Cleared starter migrations and created the initial migration 'Init'."

if ($KeepStarterHistory) {
    Write-Host "Kept the starter's own history docs (-KeepStarterHistory)."
}
else {
    # 整個目錄刪除：腳手架自己的規劃與設計稿，對衍生專案沒有用處。
    $prunedDocDirectories = @("planning", "superpowers")
    # 只清內容、保留 README.md：docs/operations/維護規範.md 要求每一次異動寫一篇 changelog，
    # 新專案要從自己的第一篇開始，所以目錄與索引骨架必須留著。
    $emptiedDocDirectories = @("changelog")
    $historyDirectories = $prunedDocDirectories + $emptiedDocDirectories

    $docsRoot = Join-Path $destinationFullPath "docs"
    $removedFiles = 0

    foreach ($name in $prunedDocDirectories) {
        $path = Join-Path $docsRoot $name
        if (Test-Path -LiteralPath $path) {
            $removedFiles += @(Get-ChildItem -LiteralPath $path -Recurse -File).Count
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    foreach ($name in $emptiedDocDirectories) {
        $path = Join-Path $docsRoot $name
        if (Test-Path -LiteralPath $path) {
            Get-ChildItem -LiteralPath $path -Recurse -File |
                Where-Object { $_.Name -ne "README.md" } |
                ForEach-Object {
                    Remove-Item -LiteralPath $_.FullName -Force
                    $removedFiles++
                }
        }
    }

    # 清完之後，索引與內文裡會留下指向已刪檔案的連結。一條通則處理三種情況：
    # 整節被刪的目錄 -> 連標題一起移除；整行只是一條索引項 -> 刪整行；
    # 夾在敘述句裡 -> 降級為純文字，句子仍然讀得通。
    $linkPattern = '\[(?<text>[^\]]*)\]\((?<target>[^)\s]+)\)'
    $removedLines = 0
    $downgradedLinks = 0

    $markdownFiles = @(Get-ChildItem -LiteralPath $docsRoot -Recurse -File -Filter "*.md")
    $rootReadme = Join-Path $destinationFullPath "readme.md"
    if (Test-Path -LiteralPath $rootReadme) {
        $markdownFiles += Get-Item -LiteralPath $rootReadme
    }

    foreach ($file in $markdownFiles) {
        $hasBom = Test-Utf8Bom -Path $file.FullName
        $original = [System.IO.File]::ReadAllText($file.FullName)
        $newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }

        $kept = New-Object System.Collections.Generic.List[string]
        $skipSection = $false

        foreach ($line in ($original -split "`r`n|`n")) {
            if ($line -match '^#{1,6}\s') {
                $skipSection = $false
                foreach ($name in $prunedDocDirectories) {
                    if ($line -match [regex]::Escape($name)) {
                        $skipSection = $true
                        break
                    }
                }
            }

            if ($skipSection) {
                $removedLines++
                continue
            }

            $links = [regex]::Matches($line, $linkPattern)
            $stale = @($links | Where-Object {
                Test-StarterHistoryLink -FileDirectory $file.Directory.FullName `
                    -Target $_.Groups['target'].Value `
                    -DocsRoot $docsRoot -HistoryDirectories $historyDirectories
            })

            if ($stale.Count -eq 0) {
                $kept.Add($line)
                continue
            }

            $trimmed = $line.TrimStart()
            $isIndexRow = $stale.Count -eq $links.Count -and
                ($trimmed.StartsWith("- ") -or $trimmed.StartsWith("* ") -or $trimmed.StartsWith("|"))

            if ($isIndexRow) {
                $removedLines++
                continue
            }

            $updated = $line
            foreach ($link in $stale) {
                $updated = $updated.Replace($link.Value, $link.Groups['text'].Value)
                $downgradedLinks++
            }

            $kept.Add($updated)
        }

        $result = $kept -join $newline
        if ($result -ne $original) {
            [System.IO.File]::WriteAllText($file.FullName, $result, (New-Object System.Text.UTF8Encoding($hasBom)))
        }
    }

    Write-Host "Pruned $removedFiles starter history doc files (docs/changelog entries, docs/planning, docs/superpowers)."
    Write-Host "Removed $removedLines stale index lines and downgraded $downgradedLinks stale links. Use -KeepStarterHistory to keep them."
}

$remainingMatches = Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    Select-String -Pattern $SourceProjectName, "DevelopmentOnly-ChangeThisJwtSigningKey", $sourceUserSecretsId, "localhost:$sourceHttpPort", "localhost:$sourceHttpsPort" -SimpleMatch

if ($remainingMatches) {
    Write-Warning "Scaffold completed, but safety checks found values that still need review:"
    $remainingMatches | ForEach-Object {
        Write-Warning "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
    }
}

Write-Host "Created starter project at $destinationFullPath"
Write-Host "Dev URLs: https://localhost:$httpsPort / http://localhost:$httpPort (Properties/launchSettings.json)"
Write-Host "Google OAuth redirect URIs to register: https://localhost:$httpsPort/signin-google , http://localhost:$httpPort/signin-google"
Write-Host "Next: see docs/guides/VS Code 開發環境與新專案上手指南.md - section 7 (branding: favicon, brand image, product name/description) and sections 8.1 / 8.3 for the manual follow-up items."
