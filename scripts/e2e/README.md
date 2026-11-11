# 實機驗證腳本（scripts/e2e）

0.10.0 起（WASM 遷移 M1）。用真的瀏覽器操作真的網站，結果寫成 JSON 與截圖，供各里程碑的測試報告引用。
不在品質關卡裡（要先啟動網站、約需數分鐘），每個里程碑完成前手動執行。

## 準備（一次）

playwright-core 不放進 repo（避免 `node_modules` 被各種掃描讀到），裝在 repo 外的任一資料夾，瀏覽器用本機的 Chrome：

```powershell
mkdir $env:TEMP\pw; cd $env:TEMP\pw; npm init -y; npm install playwright-core
```

需要 Node.js 22.5 以上（用內建的 `node:sqlite` 唯讀查詢隔離資料庫）。

## 執行

1. 以隔離資料夾啟動網站（全新資料庫；support 的暫時密碼會印在主控台）：

   ```powershell
   pwsh ./scripts/Start-E2EHost.ps1 -SupportPassword '<暫時密碼>'
   ```

2. 另開一個終端機執行情境：

   ```powershell
   $env:PLAYWRIGHT_CORE_DIR = "$env:TEMP\pw\node_modules\playwright-core"
   $env:E2E_SUPPORT_PASSWORD = '<同一組暫時密碼>'
   $env:E2E_DB_FILE = '<隔離資料夾>\DB\BackendDB.db'
   $env:E2E_OUT_DIR = '<放結果與截圖的資料夾>'
   node ./scripts/e2e/m01-tokens.mjs        # M1 情境＋全部路由煙霧測試
   node ./scripts/e2e/m02-shell.mjs         # M2 情境＋全部路由煙霧測試
   node ./scripts/e2e/m03-views.mjs server  # M3 外觀比對用的固定畫面（改版前後各跑一次，前綴不同）
   node ./scripts/e2e/m03-crud.mjs          # M3 情境＋全部路由煙霧測試（會先佈置 m03-views 的固定資料）
   node ./scripts/e2e/m04-views.mjs server  # M4 外觀比對用的固定畫面（改版前後各跑一次，前綴不同）
   node ./scripts/e2e/m04-projects.mjs      # M4 情境 P1～P18＋全部路由煙霧測試（會先佈置 m04-views 的固定資料）
   node ./scripts/e2e/m05-views.mjs server  # M5 外觀比對用的固定畫面（改版前後各跑一次，前綴不同）
   node ./scripts/e2e/m05-admin.mjs         # M5 情境 A1～A13＋全部路由煙霧測試（會先佈置 m05-views 的固定資料）
   node ./scripts/e2e/m06-views.mjs server  # M6 外觀比對用的固定畫面（改版前後各跑一次，前綴不同）
   node ./scripts/e2e/m06-ai.mjs            # M6 AI 情境 M6-A1～A9（真實模型，需要 AI 設定）
   node ./scripts/e2e/m06-ai.mjs quota      # M6 超過用量上限情境 M6-Q1（主機要另外啟動，見下方）
   node ./scripts/e2e/m06-ops.mjs           # M6 操作情境 M6-O1～O8＋全部路由煙霧測試
   node ./scripts/e2e/m07-views.mjs server  # M7 外觀比對用的固定畫面（改版前後各跑一次，前綴不同）
   node ./scripts/e2e/m07-ops.mjs           # M7 情境 M7-P1～P9＋全部路由煙霧測試（會先佈置 m07-views 的固定資料）
   node ./scripts/e2e/m08-views.mjs server  # M8 外觀比對用的固定畫面（改版前後各跑一次，前綴不同）
   node ./scripts/e2e/m08-agents.mjs        # M8 情境 M8-G1～G9（真實模型，需要 AI 設定）＋全部路由煙霧測試
   node ./scripts/e2e/m08-agents.mjs nopush # M8 關閉推播情境 M8-N1（主機要另外啟動，見下方）
   node ./scripts/e2e/smoke-routes.mjs      # 只跑全部路由煙霧測試
   ```

| 環境變數 | 說明 |
|---|---|
| `PLAYWRIGHT_CORE_DIR` | playwright-core 套件資料夾（必填） |
| `E2E_SUPPORT_PASSWORD` | support 的暫時密碼（必填） |
| `E2E_DB_FILE` | 隔離資料庫檔案（情境腳本必填；煙霧測試不需要） |
| `E2E_OUT_DIR` | 結果與截圖的輸出資料夾，預設 `./e2e-out` |
| `E2E_BASE_URL` | 網站網址，預設 `https://localhost:7144` |
| `E2E_CHROME` | Chrome 執行檔，預設 `C:/Program Files/Google/Chrome/Application/chrome.exe` |
| `E2E_HEADED` | 設為 `1` 時顯示瀏覽器視窗 |

**M6 的 AI 情境（0.10.5 起）**：`m06-ai.mjs` 呼叫真實模型，`Start-E2EHost.ps1` 以 Development 執行，金鑰照常讀 User Secrets（腳本不碰金鑰）；沒有 AI 設定時這些情境會失敗。
`quota` 參數只跑「超過每人每日用量上限」，主機要以極低的上限啟動（同一個終端機先設環境變數再啟動，`Start-E2EHost.ps1` 會傳給網站）：

```powershell
$env:AiQuotaSettings__PerUserDailyTwd = '1'
pwsh ./scripts/Start-E2EHost.ps1 -SupportPassword '<暫時密碼>'
```

跑完記得關掉這個主機、清掉環境變數，再用一般主機跑其他情境。結果分別寫到 `E2E_OUT_DIR` 的 `m06-results.json`、`m06-quota-results.json`、`m06-ops-results.json`。

**M7 的備份檔（0.10.6 起）**：`m07-views.mjs` 在隔離資料夾底下的 `Backup` 目錄（`E2E_DB_FILE` 往上兩層，即 `Start-E2EHost.ps1` 設定的 `BackupPath`）放兩個空的 zip（`backup-20261101-090000.zip`、`backup-20261101-100000-2.zip`），並佈置三則固定公告與一般使用者 `e2e-m7-user`；同一個資料庫可以重跑。`m07-ops.mjs` 的立即備份會真的執行系統備份作業。結果寫到 `m07-results.json`。

**M8 的 Agent 與推播情境（0.10.7 起）**：`m08-agents.mjs` 呼叫真實模型（同 M6，金鑰照常讀 User Secrets），會產生測試專案、以排程觸發一次「系統管理助理巡檢」（最多等 10 分鐘），整體要跑好幾分鐘。真實模型常會先回問「請確認是否要修改？」才呼叫寫入工具，腳本遇到時會回答「確認」再等核准窗。
`nopush` 參數只跑「關閉推播仍可輪詢」（M8-N1：另一個瀏覽器的鈴鐺在 75 秒內由輪詢更新），主機要以關閉推播啟動：

```powershell
$env:NotificationSettings__PushEnabled = 'false'
pwsh ./scripts/Start-E2EHost.ps1 -SupportPassword '<暫時密碼>'
```

跑完記得關掉這個主機、清掉環境變數。結果分別寫到 `E2E_OUT_DIR` 的 `m08-results.json`、`m08-nopush-results.json`（一般模式另寫 `smoke-results.json`）。

`Start-E2EHost.ps1` 0.10.5 起把 API 限流放寬到每分鐘 1000 次（`RateLimit__ApiRequestsPerMinute`）：腳本換頁比人快得多，正式的每人每分鐘 120 次會被打滿。

⚠️ 情境腳本會改隔離資料庫（例如暫時把 support 標成必須改密碼、變更 support 的密碼），只能對 `Start-E2EHost.ps1` 啟動的網站執行，不可指向開發或正式資料庫。

## 檔案

| 檔案 | 內容 |
|---|---|
| `common.mjs` | 共用：啟動瀏覽器、登入、在頁面內呼叫 API、唯讀查詢資料庫、截圖、記錄結果；0.10.3 起另有 `createScopedUser`（建一個指定權限與團隊的帳號）、`isWasmPage`（判斷目前頁面是不是 WASM）、`apiAsPageUser`（以頁面上那個人的 Token 呼叫 API），原本在 `m03-crud.mjs` |
| `smoke-routes.mjs` | 全部 37 個路由的煙霧測試：每頁開得起來、沒有錯誤列或無權限畫面、沒有頁面錯誤，並截圖；0.10.5 起登入後的頁面被丟回登入頁、或停在「請求過於頻繁」的限流提示，也算失敗 |
| `m01-tokens.mjs` | M1 情境：Cookie 換 Token、刷新輪替與重用偵測、登出撤銷、強制改密碼擋 API、改密碼後其他登入立即失效、登入 Token 清理排程 |
| `crud-ui.mjs` | 資料維護頁的共用操作：開清單、新增、修改、刪除、還原、永久刪除、確認窗、右下角通知（0.10.2 起） |
| `crud-generated.mjs` | CRUD 產生器產生的頁面實跑（`E2E_CRUD_ROUTES` 指定路由）；由 `scripts/Test-CrudGenerator.ps1` 在暫存 worktree 呼叫 |
| `m03-views.mjs` | M3 外觀比對：佈置固定的分類、團隊、唯讀角色，拍分類頁與團隊頁的 8 個畫面（清單、修改窗、已刪除、部門樹、搜尋、唯讀） |
| `m03-crud.mjs` | M3 情境：分類完整流程、重名、兩個分頁同時修改、匯出、異動歷程、團隊批次刪除、改名同步、資料保留頁「前往」、Server／WASM 切換、團隊範圍、唯讀角色、沒有權限開頁的稽核 |
| `m04-views.mjs` | M4 外觀比對：沿用 m03-views 的團隊、分類與唯讀角色（唯讀角色另加「專案項目:view」），佈置固定的專案與附件紀錄，拍專案頁的 5 個畫面（清單、修改窗、修改窗附件、已刪除、唯讀） |
| `m04-projects.mjs` | M4 情境 P1～P18：完整流程與多檔上傳、下載內容比對、票證重放、修改時移除附件、約 200 MB 的大檔、超過 1 GB 被擋、副檔名被擋、重名、兩個分頁同時修改、刪除／還原／永久刪除（實體檔移除）、匯出、批次刪除、異動歷程、資料保留頁「前往」、Server／WASM 切換、沒有未處理錯誤、團隊範圍（看不到也申請不到票證）、唯讀角色、沒有權限開頁的稽核。P5 用 `fsutil` 建 1.1 GB 的暫存檔（只選檔、不上傳） |
| `m05-views.mjs` | M5 外觀比對（0.10.4 起）：沿用 m03-views 的團隊與唯讀角色，佈置固定的使用者（一般、鎖定、兩步驟驗證、已刪除）、角色、稽核紀錄與寄信紀錄，拍使用者、角色、稽核紀錄、寄信紀錄頁的 8 個畫面（使用者清單、修改窗、已刪除、角色清單、修改窗、稽核紀錄、稽核明細、寄信紀錄）。為了不讓截圖露出個資，會把隔離資料庫裡 support 的 Email 改成 `support@e2e.test` |
| `m05-admin.mjs` | M5 情境 A1～A13（0.10.4 起）：使用者完整流程、改別人的角色後對方被登出、停用後對方被登出、解除鎖定、複製使用者（產生密碼、交付窗、稽核記 `copiedFrom`）、匯出使用者 Excel、角色權限矩陣存檔、稽核查詢／明細／完整性驗證／匯出、寄信紀錄篩選、Server／WASM 切換、沒有未處理錯誤、改自己的角色後這台裝置仍維持登入、非管理員直打管理 API 得 403 |
| `m06-views.mjs` | M6 外觀比對（0.10.5 起）：佈置固定的例外、AI 對話紀錄、Token 用量與提示詞版本，另寫一個固定日期（2026-11-01）的日誌檔，拍 9 個畫面（日誌檢視、日誌等級、系統例外、例外明細、AI 對話紀錄、Token 用量、Token 明細、AI 提示詞、提示詞編輯窗）。資料只佈置一次，同一個資料庫可以重跑 |
| `m06-ai.mjs` | M6 AI 情境（0.10.5 起，真實模型）：M6-A1 例外 AI 分析逐字出現、A2 追問、A3 回覆裡的 `javascript:` 連結被中和、A4 匯出 PDF、A5 稽核筆數與內容不含對話、A6 關窗＝取消且 AI 對話紀錄看得到、A7 停止產生保留部分內容並標示已中止、A8 日誌 AI 分析與匯出 PDF、A9 沒有未處理的瀏覽器錯誤；`quota` 參數只跑 M6-Q1 超過用量上限（沒有呼叫模型） |
| `m06-ops.mjs` | M6 操作情境（0.10.5 起，不呼叫 AI）：M6-O1 日誌等級套用與還原、O2 提示詞存新版本與切回內建預設、O3 系統例外排序／匯出同序／刪除、O4 AI 對話紀錄深連結與匯出、O5 Token 用量匯出與明細窗連到 AI 對話紀錄、O6 日誌快速區間與匯出含 BOM、O7 非管理員直打 M6 的 API 得 403、O8 沒有未處理的瀏覽器錯誤；最後跑全部路由的煙霧測試 |
| `m07-views.mjs` | M7 外觀比對（0.10.6 起）：佈置固定的公告、兩個空 zip 備份檔與一般使用者，拍 9 個畫面（排程作業、系統健康、系統備份、資料保留、資料庫用量、系統參數、公告管理、首頁〔管理員〕、首頁〔一般使用者〕）。系統健康、資料庫用量、排程的「下次執行」含即時數值，差異集中在數值處 |
| `m07-ops.mjs` | M7 情境（0.10.6 起）：M7-P1 排程立即執行後狀態自動更新（不重新整理）、P2 立即備份→清單自動出現→以一次性票證下載 zip（第二次 404、舊的 Cookie 下載網址 404）、P3 修改系統名稱立即生效並還原、P4 公告新增後同一個分頁的橫幅立即出現、P5 另一個分頁（一般使用者）切回時看得到、P6 首頁小工具依權限（一般使用者直接呼叫排程小工具的 API 得 403）、P7 系統健康／資料保留／資料庫用量在 WASM 開得起來、P8 非管理員直打 M7 的 13 個 API 得 403、P9 沒有未處理的瀏覽器錯誤；最後跑全部路由的煙霧測試 |
| `m08-views.mjs` | M8 外觀比對（0.10.7 起）：拍 4 個初始畫面（Agent 測試台、Agent 工作流程、系統管理助理、巡檢報告窗）。對話與執行過程含 AI 輸出，不做逐像素比對，改由 `m08-agents.mjs` 的情境驗證 |
| `m08-agents.mjs` | M8 情境（0.10.7 起，真實模型）：M8-G1 Agent 測試台在 WASM、產生測試專案、G2 寫入工具跳出核准窗 → 核准 → 真的寫入且稽核一筆、G3 核准中重新整理回到同一段對話、核准窗再跳出 → 拒絕 → 沒有寫入、G4 兩個分頁同時送出同一段對話（後到的排隊，兩則都完成）、G5 工作流程「專案狀態報告」人工介入中重新整理可接回直到完成、G6 系統管理助理不可復原的動作要輸入「確認」才能核准（這次按拒絕）、G7 排程巡檢產生報告且 `?patrolReport=` 直接打開、G8 推播：另一個瀏覽器的鈴鐺 2 秒內更新、G9 沒有未處理的瀏覽器錯誤；最後跑全部路由的煙霧測試。`nopush` 參數只跑 M8-N1 |
| `m02-shell.mjs` | M2 情境：WASM 版個人資料頁與外框（選單、通知、公告、操作說明）、Server／WASM 頁面互相切換、斷線不卡死、從 WASM 頁登出、換人登入、強制改密碼導向、跨模式換頁的開頁稽核只記一筆（`Start-E2EHost.ps1` 會開啟瀏覽類稽核） |
