param(
    # dotnet test --results-directory 指定的資料夾；底下每個測試專案各有一份 coverage.cobertura.xml。
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory
)

# 覆蓋率門檻（0.10.34 起，品質關卡的 Coverage 步驟）：只防退步，不要求為了數字補測。
# 1. 伺服器端（WASM 前端 MyProject.Web.Client 以外）整體的行／分支覆蓋率下限。
# 2. 安全關鍵檔案（權限、資料範圍、登入與 Token、稽核）的逐檔下限。
#
# 數字是 0.10.34 補測後以品質關卡（Release）實測、無條件捨去到整數的值。
# 覆蓋率合理下降（例如刪掉有測試的程式）時，調低這裡的數字並在 commit 說明原因；
# 補了測試讓數字上升時可以順手調高。檔案改名或搬移時，這裡的路徑要一起改（找不到會失敗）。
# 算法與覆蓋率報告相同：依「檔案」彙總，同一行出現在多個類別（async 狀態機、lambda）時取最大值。

$ErrorActionPreference = "Stop"

$serverMinimum = @{ Line = 92; Branch = 81 }

$criticalFiles = @(
    # 權限
    @{ Path = "MyProject.Web/Filters/HasPermissionAttribute.cs"; Line = 100; Branch = 81 }
    @{ Path = "MyProject.Business/Services/Other/PermissionChecker.cs"; Line = 100; Branch = 93 }
    @{ Path = "MyProject.Business/Services/Other/RolePermissionService.cs"; Line = 98; Branch = 88 }
    @{ Path = "MyProject.Models/Others/PermissionRules.cs"; Line = 100; Branch = 100 }
    # 資料範圍
    @{ Path = "MyProject.Web/Auth/RecordAccessScopeProvider.cs"; Line = 96; Branch = 68 }
    @{ Path = "MyProject.Business/Helpers/RecordTeamScope.cs"; Line = 91; Branch = 92 }
    # 登入與 Token
    @{ Path = "MyProject.Web/Auth/JwtTokenService.cs"; Line = 100; Branch = 100 }
    @{ Path = "MyProject.Business/Services/Other/RefreshTokenStore.cs"; Line = 95; Branch = 88 }
    @{ Path = "MyProject.Web/Auth/WebSessionTokens.cs"; Line = 94; Branch = 61 }
    @{ Path = "MyProject.Web/Auth/ApiActorMiddleware.cs"; Line = 94; Branch = 85 }
    @{ Path = "MyProject.Business/Services/Other/MyUserServiceLogin.cs"; Line = 97; Branch = 98 }
    @{ Path = "MyProject.Business/Helpers/SecurePasswordHasher.cs"; Line = 79; Branch = 78 }
    @{ Path = "MyProject.Business/Services/Other/SecurityStampService.cs"; Line = 100; Branch = 95 }
    @{ Path = "MyProject.Business/Services/Other/TwoFactorService.cs"; Line = 99; Branch = 92 }
    # 稽核
    @{ Path = "MyProject.Business/Services/Other/AuditLogService.cs"; Line = 100; Branch = 96 }
    @{ Path = "MyProject.Business/Services/Other/AuditIntegrity/AuditChainStore.cs"; Line = 95; Branch = 86 }
    @{ Path = "MyProject.Business/Services/Other/AuditIntegrity/AuditHashV1.cs"; Line = 100; Branch = 92 }
    @{ Path = "MyProject.Business/Services/Other/AuditIntegrity/AuditIntegrityVerifier.cs"; Line = 94; Branch = 85 }
)

$excludedPackages = @("MyProject.Web.Client")

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    throw "No coverage.cobertura.xml found under $ResultsDirectory. Run dotnet test with --collect:`"XPlat Code Coverage`" first."
}

# 檔案 → 行號 → [執行次數, 已覆蓋分支, 分支總數]（多份報告、多個類別取最大值）
$files = @{}
foreach ($report in $reports) {
    [xml]$xml = Get-Content -LiteralPath $report.FullName -Raw -Encoding UTF8
    foreach ($package in $xml.coverage.packages.package) {
        if ($excludedPackages -contains $package.name) {
            continue
        }

        foreach ($class in $package.classes.class) {
            $path = $class.filename.Replace('\', '/')
            if (-not $files.ContainsKey($path)) {
                $files[$path] = @{}
            }

            $lines = $files[$path]
            foreach ($line in $class.lines.line) {
                $number = [int]$line.number
                $hits = [long]$line.hits
                $covered = 0
                $total = 0
                if ($line.branch -eq "True" -and $line.'condition-coverage' -match '\((\d+)/(\d+)\)') {
                    $covered = [int]$Matches[1]
                    $total = [int]$Matches[2]
                }

                if ($lines.ContainsKey($number)) {
                    $current = $lines[$number]
                    $lines[$number] = @([math]::Max($current[0], $hits), [math]::Max($current[1], $covered), [math]::Max($current[2], $total))
                }
                else {
                    $lines[$number] = @($hits, $covered, $total)
                }
            }
        }
    }
}

function Get-FileCoverage([hashtable]$Lines) {
    $result = [ordered]@{ LinesValid = 0; LinesCovered = 0; BranchesValid = 0; BranchesCovered = 0 }
    foreach ($value in $Lines.Values) {
        $result.LinesValid++
        if ($value[0] -gt 0) { $result.LinesCovered++ }
        $result.BranchesCovered += $value[1]
        $result.BranchesValid += $value[2]
    }

    return $result
}

function Format-Rate([int]$Covered, [int]$Valid) {
    if ($Valid -eq 0) { return $null }
    return [math]::Round(100.0 * $Covered / $Valid, 2)
}

$failures = [System.Collections.Generic.List[string]]::new()

# 1. 伺服器端整體
$server = [ordered]@{ LinesValid = 0; LinesCovered = 0; BranchesValid = 0; BranchesCovered = 0 }
foreach ($lines in $files.Values) {
    $coverage = Get-FileCoverage $lines
    foreach ($key in @($server.Keys)) { $server[$key] += $coverage[$key] }
}

$serverLine = Format-Rate $server.LinesCovered $server.LinesValid
$serverBranch = Format-Rate $server.BranchesCovered $server.BranchesValid
Write-Host ("Server-side coverage: line {0}% ({1}/{2}), branch {3}% ({4}/{5}); minimum line {6}%, branch {7}%." -f `
    $serverLine, $server.LinesCovered, $server.LinesValid, $serverBranch, $server.BranchesCovered, $server.BranchesValid, `
    $serverMinimum.Line, $serverMinimum.Branch)
if ($serverLine -lt $serverMinimum.Line) {
    $failures.Add("Server-side line coverage $serverLine% is below $($serverMinimum.Line)%.")
}
if ($serverBranch -lt $serverMinimum.Branch) {
    $failures.Add("Server-side branch coverage $serverBranch% is below $($serverMinimum.Branch)%.")
}

# 2. 安全關鍵檔案
$rows = foreach ($critical in $criticalFiles) {
    $matched = @($files.Keys | Where-Object { $_ -eq $critical.Path -or $_.EndsWith("/" + $critical.Path) })
    if ($matched.Count -ne 1) {
        $failures.Add("Critical file '$($critical.Path)' matched $($matched.Count) files in the coverage report; update the list in scripts/Test-CoverageThreshold.ps1.")
        continue
    }

    $coverage = Get-FileCoverage $files[$matched[0]]
    $line = Format-Rate $coverage.LinesCovered $coverage.LinesValid
    $branch = Format-Rate $coverage.BranchesCovered $coverage.BranchesValid
    $ok = ($null -eq $line -or $line -ge $critical.Line) -and ($null -eq $branch -or $branch -ge $critical.Branch)
    if (-not $ok) {
        $failures.Add("Critical file '$($critical.Path)': line $line% (minimum $($critical.Line)%), branch $branch% (minimum $($critical.Branch)%).")
    }

    [pscustomobject]@{
        File = $critical.Path
        Line = if ($null -eq $line) { "-" } else { "$line% / $($critical.Line)%" }
        Branch = if ($null -eq $branch) { "-" } else { "$branch% / $($critical.Branch)%" }
        Result = if ($ok) { "OK" } else { "BELOW" }
    }
}

Write-Host ""
Write-Host "Critical files (actual / minimum):"
$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    # 訊息用英文：中文在繁中 Windows 的主控台（cp950）會變亂碼。說明見 docs/guides/測試指南.md §4。
    throw "Coverage check failed ($($failures.Count) item(s)). See section 4 (coverage) of the testing guide under docs/guides."
}

Write-Host "Coverage thresholds passed."
