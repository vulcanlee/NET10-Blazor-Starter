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

⚠️ 情境腳本會改隔離資料庫（例如暫時把 support 標成必須改密碼、變更 support 的密碼），只能對 `Start-E2EHost.ps1` 啟動的網站執行，不可指向開發或正式資料庫。

## 檔案

| 檔案 | 內容 |
|---|---|
| `common.mjs` | 共用：啟動瀏覽器、登入、在頁面內呼叫 API、唯讀查詢資料庫、截圖、記錄結果；0.10.3 起另有 `createScopedUser`（建一個指定權限與團隊的帳號）、`isWasmPage`（判斷目前頁面是不是 WASM）、`apiAsPageUser`（以頁面上那個人的 Token 呼叫 API），原本在 `m03-crud.mjs` |
| `smoke-routes.mjs` | 全部 37 個路由的煙霧測試：每頁開得起來、沒有錯誤列或無權限畫面、沒有頁面錯誤，並截圖 |
| `m01-tokens.mjs` | M1 情境：Cookie 換 Token、刷新輪替與重用偵測、登出撤銷、強制改密碼擋 API、改密碼後其他登入立即失效、登入 Token 清理排程 |
| `crud-ui.mjs` | 資料維護頁的共用操作：開清單、新增、修改、刪除、還原、永久刪除、確認窗、右下角通知（0.10.2 起） |
| `crud-generated.mjs` | CRUD 產生器產生的頁面實跑（`E2E_CRUD_ROUTES` 指定路由）；由 `scripts/Test-CrudGenerator.ps1` 在暫存 worktree 呼叫 |
| `m03-views.mjs` | M3 外觀比對：佈置固定的分類、團隊、唯讀角色，拍分類頁與團隊頁的 8 個畫面（清單、修改窗、已刪除、部門樹、搜尋、唯讀） |
| `m03-crud.mjs` | M3 情境：分類完整流程、重名、兩個分頁同時修改、匯出、異動歷程、團隊批次刪除、改名同步、資料保留頁「前往」、Server／WASM 切換、團隊範圍、唯讀角色、沒有權限開頁的稽核 |
| `m04-views.mjs` | M4 外觀比對：沿用 m03-views 的團隊、分類與唯讀角色（唯讀角色另加「專案項目:view」），佈置固定的專案與附件紀錄，拍專案頁的 5 個畫面（清單、修改窗、修改窗附件、已刪除、唯讀） |
| `m04-projects.mjs` | M4 情境 P1～P18：完整流程與多檔上傳、下載內容比對、票證重放、修改時移除附件、約 200 MB 的大檔、超過 1 GB 被擋、副檔名被擋、重名、兩個分頁同時修改、刪除／還原／永久刪除（實體檔移除）、匯出、批次刪除、異動歷程、資料保留頁「前往」、Server／WASM 切換、沒有未處理錯誤、團隊範圍（看不到也申請不到票證）、唯讀角色、沒有權限開頁的稽核。P5 用 `fsutil` 建 1.1 GB 的暫存檔（只選檔、不上傳） |
| `m05-views.mjs` | M5 外觀比對（0.10.4 起）：沿用 m03-views 的團隊與唯讀角色，佈置固定的使用者（一般、鎖定、兩步驟驗證、已刪除）、角色、稽核紀錄與寄信紀錄，拍使用者、角色、稽核紀錄、寄信紀錄頁的 8 個畫面（使用者清單、修改窗、已刪除、角色清單、修改窗、稽核紀錄、稽核明細、寄信紀錄）。為了不讓截圖露出個資，會把隔離資料庫裡 support 的 Email 改成 `support@e2e.test` |
| `m05-admin.mjs` | M5 情境 A1～A13（0.10.4 起）：使用者完整流程、改別人的角色後對方被登出、停用後對方被登出、解除鎖定、複製使用者（產生密碼、交付窗、稽核記 `copiedFrom`）、匯出使用者 Excel、角色權限矩陣存檔、稽核查詢／明細／完整性驗證／匯出、寄信紀錄篩選、Server／WASM 切換、沒有未處理錯誤、改自己的角色後這台裝置仍維持登入、非管理員直打管理 API 得 403 |
| `m02-shell.mjs` | M2 情境：WASM 版個人資料頁與外框（選單、通知、公告、操作說明）、Server／WASM 頁面互相切換、斷線不卡死、從 WASM 頁登出、換人登入、強制改密碼導向、跨模式換頁的開頁稽核只記一筆（`Start-E2EHost.ps1` 會開啟瀏覽類稽核） |
