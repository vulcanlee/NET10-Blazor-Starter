<#
.SYNOPSIS
    驗證 CRUD 產生器（scripts/New-CrudModule.ps1）產出的模組能建置、全部測試通過，而且重跑不會重複登記（0.9.110 起）。

.DESCRIPTION
    在暫存的 git worktree 裡（以目前工作目錄的內容為準，含尚未提交的變更）依序：
      1. 產生三個範例模組：一般、-WithTeams、-AdminOnly（掛在系統管理群組）。
      2. 建置、格式檢查、執行全部測試、檢查文件編碼。
      2a. 實跑（0.10.2 起）：在 worktree 以 Start-E2EHost.ps1 啟動隔離的網站，用 Playwright 打開三個產生出來的頁面（WASM），
          各做一次新增、修改、刪除、顯示已刪除、還原（scripts/e2e/crud-generated.mjs）。需要 PLAYWRIGHT_CORE_DIR（見 scripts/e2e/README.md）。
      3. 以 -Force 重跑三個產生指令，確認沒有任何檔案再被改動（登記不重複）。
      4. 刪除 worktree（-KeepWorktree 時保留，方便啟動網站實際操作）。
    不會改動你的工作目錄與 git index。耗時數分鐘，所以不在品質關卡裡；修改產生器或樣板後必跑。

.PARAMETER KeepWorktree
    結束後保留 worktree，並印出路徑。

.PARAMETER SkipRealRun
    略過實跑（例如沒有安裝 playwright-core 的機器）；只驗證產生、建置、測試與冪等。
#>
[CmdletBinding()]
param(
    [switch]$KeepWorktree,
    [switch]$SkipRealRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-wt-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "==> $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $action
    $results.Add([pscustomobject]@{ Step = $name; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) })
}

function Invoke-Native([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') 失敗（exit $LASTEXITCODE）" }
}

function Get-TreeState {
    # 目前 worktree 的完整內容（含未追蹤檔案），用暫存 index 計算，不影響任何人的 index。
    $index = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-index-" + [guid]::NewGuid().ToString('N'))
    try {
        $env:GIT_INDEX_FILE = $index
        Invoke-Native git @('-C', $worktree, '-c', 'core.safecrlf=false', 'add', '-A')
        return (git -C $worktree write-tree)
    } finally {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue
    }
}

# 以 hashtable 傳參數（陣列 splatting 只會變成位置參數）。
$modules = @(
    @{ Name = 'GenSample'; DisplayName = '產生器範例' },
    @{ Name = 'GenTeamSample'; DisplayName = '產生器團隊範例'; WithTeams = $true; Icon = 'inventory_2' },
    @{ Name = 'GenAdminSample'; DisplayName = '產生器管理範例'; AdminOnly = $true; MenuGroupId = 8 }
)

try {
    Invoke-Step '建立暫存 worktree（目前工作目錄的內容）' {
        $index = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-index-" + [guid]::NewGuid().ToString('N'))
        try {
            $env:GIT_INDEX_FILE = $index
            Invoke-Native git @('-C', $repoRoot, 'read-tree', 'HEAD')
            Invoke-Native git @('-C', $repoRoot, '-c', 'core.safecrlf=false', 'add', '-A')
            $tree = git -C $repoRoot write-tree
        } finally {
            Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue
        }
        $commit = git -C $repoRoot commit-tree $tree -p HEAD -m 'Test-CrudGenerator snapshot'
        Invoke-Native git @('-C', $repoRoot, 'worktree', 'add', '--detach', $worktree, $commit)
    }

    $generator = Join-Path $worktree 'scripts/New-CrudModule.ps1'
    $script:first = $true
    foreach ($module in $modules) {
        Invoke-Step "產生 $($module.Name)" {
            $arguments = $module.Clone()
            if (-not $script:first) { $arguments['Force'] = $true }
            & $generator @arguments
            $script:first = $false
        }
    }

    $solution = Join-Path $worktree 'src/MyProject/MyProject.slnx'
    Invoke-Step '建置' { Invoke-Native dotnet @('build', $solution, '-v', 'q', '-nologo') }
    Invoke-Step '格式檢查' { Invoke-Native dotnet @('format', 'whitespace', $solution, '--verify-no-changes') }
    Invoke-Step '全部測試' { Invoke-Native dotnet @('test', $solution, '--no-build', '-v', 'q', '-nologo') }
    Invoke-Step '文件編碼' { Invoke-Native pwsh @('-NoProfile', '-File', (Join-Path $worktree 'scripts/Test-DocsEncoding.ps1')) }

    if (-not $SkipRealRun) {
        if (-not $env:PLAYWRIGHT_CORE_DIR) {
            throw '實跑需要 PLAYWRIGHT_CORE_DIR（playwright-core 套件資料夾，見 scripts/e2e/README.md）；只想驗證建置與測試請加 -SkipRealRun。'
        }

        Invoke-Step '實跑產生的頁面（WASM）' {
            if (Get-Process MyProject.Web -ErrorAction SilentlyContinue) {
                throw '已有 MyProject.Web 在執行（佔用 https://localhost:7144），請先關閉再實跑。'
            }

            $e2eRoot = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-e2e-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
            # 一次性的暫時密碼：只放在這個行程與子行程的環境變數（E2E_SUPPORT_PASSWORD），不寫入檔案、也不放在命令列（其他人看得到行程清單）。
            $env:E2E_SUPPORT_PASSWORD = 'Gen!' + [guid]::NewGuid().ToString('N').Substring(0, 12) + 'Aa9'
            $hostProcess = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @(
                '-NoProfile', '-File', (Join-Path $worktree 'scripts/Start-E2EHost.ps1'), '-Root', $e2eRoot)
            try {
                $deadline = (Get-Date).AddMinutes(5)
                $ready = $false
                while (-not $ready -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Seconds 3
                    try {
                        $ready = (Invoke-WebRequest -Uri 'https://localhost:7144/Auths/Login' -SkipCertificateCheck -TimeoutSec 10).StatusCode -eq 200
                    } catch {
                        $ready = $false
                    }
                }

                if (-not $ready) { throw '網站在 5 分鐘內沒有啟動（日誌在 ' + $e2eRoot + '\logs）。' }

                $env:E2E_DB_FILE = Join-Path $e2eRoot 'DB/BackendDB.db'
                $env:E2E_OUT_DIR = Join-Path $e2eRoot 'e2e-out'
                # 與產生器的預設路由規則相同（/<Name 小寫>s；上面三個模組都沒有另給 -Plural／-Route）。
                $env:E2E_CRUD_ROUTES = ($modules | ForEach-Object { '/' + $_.Name.ToLowerInvariant() + 's' }) -join ','
                Invoke-Native node @((Join-Path $worktree 'scripts/e2e/crud-generated.mjs'))
                Write-Host "實跑結果與截圖：$($env:E2E_OUT_DIR)"
            } finally {
                Remove-Item Env:E2E_SUPPORT_PASSWORD, Env:E2E_DB_FILE, Env:E2E_OUT_DIR, Env:E2E_CRUD_ROUTES -ErrorAction SilentlyContinue
                Get-Process MyProject.Web -ErrorAction SilentlyContinue | Stop-Process -Force
                if (-not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
            }
        }
    }

    Invoke-Step '重跑產生器（不可再改動任何檔案）' {
        $before = Get-TreeState
        foreach ($module in $modules) {
            $arguments = $module.Clone()
            $arguments['Force'] = $true
            & $generator @arguments | Out-Null
        }
        $after = Get-TreeState
        if ($before -ne $after) {
            git -C $worktree diff --stat
            throw '重跑產生器改動了檔案：登記不是冪等的。'
        }
    }

    Write-Host ''
    $results | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host 'CRUD 產生器驗證通過。' -ForegroundColor Green
} finally {
    if ($KeepWorktree) {
        Write-Host "worktree 保留在：$worktree（用完請執行 git worktree remove --force `"$worktree`"）"
    } elseif (Test-Path -LiteralPath $worktree) {
        git -C $repoRoot worktree remove --force $worktree
    }
}
