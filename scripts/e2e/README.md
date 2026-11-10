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
| `common.mjs` | 共用：啟動瀏覽器、登入、在頁面內呼叫 API、唯讀查詢資料庫、截圖、記錄結果 |
| `smoke-routes.mjs` | 全部 37 個路由的煙霧測試：每頁開得起來、沒有錯誤列或無權限畫面、沒有頁面錯誤，並截圖 |
| `m01-tokens.mjs` | M1 情境：Cookie 換 Token、刷新輪替與重用偵測、登出撤銷、強制改密碼擋 API、改密碼後其他登入立即失效、登入 Token 清理排程 |
| `m02-shell.mjs` | M2 情境：WASM 版個人資料頁與外框（選單、通知、公告、操作說明）、Server／WASM 頁面互相切換、斷線不卡死、從 WASM 頁登出、換人登入、強制改密碼導向、跨模式換頁的開頁稽核只記一筆（`Start-E2EHost.ps1` 會開啟瀏覽類稽核） |
