<#
.SYNOPSIS
    以隔離的資料目錄啟動網站，供實機驗證（Playwright）使用（0.10.0 起，WASM 遷移 M1）。

.DESCRIPTION
    所有外部路徑（資料庫、上傳、日誌、金鑰環、備份……共 12 個 ExternalFileSystem 路徑）與 NLog 日誌
    一律導到暫存資料夾，不會寫進開發機的 C:\temp 或正式路徑；每次都是全新的資料庫。
    support 帳號的密碼由 -SupportPassword 指定，沒指定時產生一組隨機的暫時密碼，只印在主控台、不寫入任何檔案。

    網站以前景執行（Ctrl+C 結束）；結束後資料夾保留，方便查資料庫與日誌，-Cleanup 則結束時刪除。
    實機驗證腳本在 scripts/e2e/，用法見該資料夾的 README.md。

.PARAMETER SupportPassword
    support 帳號的暫時密碼（只在這次執行有效）。

.PARAMETER Root
    隔離資料夾；預設在系統暫存目錄下新建一個。

.PARAMETER LaunchProfile
    launchSettings.json 的設定檔名稱，預設 https（https://localhost:7144）。

.PARAMETER Cleanup
    網站結束後刪除隔離資料夾。
#>
[CmdletBinding()]
param(
    [string]$SupportPassword,
    [string]$Root,
    [string]$LaunchProfile = 'https',
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$webProject = Join-Path $repoRoot 'src/MyProject/MyProject.Web'

if (-not $Root) {
    $Root = Join-Path ([IO.Path]::GetTempPath()) ("myproject-e2e-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
New-Item -ItemType Directory -Force -Path $Root | Out-Null

if (-not $SupportPassword) {
    # 符合預設密碼原則：大小寫、數字、符號都有，長度足夠。
    $SupportPassword = 'E2e!' + [guid]::NewGuid().ToString('N').Substring(0, 12) + 'Aa9'
}

$environment = @{
    ASPNETCORE_ENVIRONMENT                               = 'Development'
    BootstrapSettings__SupportPassword                  = $SupportPassword
    NLog__BasePath                                      = (Join-Path $Root 'logs')
    SystemSettings__ExternalFileSystem__DatabasePath    = (Join-Path $Root 'DB')
    SystemSettings__ExternalFileSystem__DownloadPath    = (Join-Path $Root 'Download')
    SystemSettings__ExternalFileSystem__UploadPath      = (Join-Path $Root 'Upload')
    SystemSettings__ExternalFileSystem__ProjectFilePath = (Join-Path $Root 'ProjectFile')
    SystemSettings__ExternalFileSystem__ExceptionPath   = (Join-Path $Root 'Exception')
    SystemSettings__ExternalFileSystem__TokenUsagePath  = (Join-Path $Root 'TokenUsage')
    SystemSettings__ExternalFileSystem__AiCallLogPath   = (Join-Path $Root 'AiCallLog')
    SystemSettings__ExternalFileSystem__AiAgentConversationPath = (Join-Path $Root 'AiAgentConversation')
    SystemSettings__ExternalFileSystem__AuditContentPath = (Join-Path $Root 'AuditContent')
    SystemSettings__ExternalFileSystem__DataProtectionKeyPath = (Join-Path $Root 'Keys')
    SystemSettings__ExternalFileSystem__BackupPath      = (Join-Path $Root 'Backup')
    SystemSettings__ExternalFileSystem__AuditKeyPath    = (Join-Path $Root 'AuditKey')
}

$saved = @{}
foreach ($key in $environment.Keys) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $environment[$key])
}

Write-Host "隔離資料夾：$Root"
Write-Host "資料庫目錄：$($environment.SystemSettings__ExternalFileSystem__DatabasePath)"
Write-Host "support 暫時密碼：$SupportPassword"
Write-Host "啟動網站（$LaunchProfile）…"

try {
    & dotnet run --project $webProject --launch-profile $LaunchProfile --no-launch-browser
}
finally {
    foreach ($key in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($key, $saved[$key])
    }

    if ($Cleanup) {
        Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
    }
}
