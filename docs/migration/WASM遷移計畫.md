# WASM 遷移計畫

- 文件版本：1.0
- 文件狀態：規劃中
- 現行系統版本：0.9.177
- 首次實作版本：—（尚未實作，預計 0.10.0 起）
- 最後核對日期：2026/11/10

## 文件目的

把 Blazor Server 改寫為 Blazor WebAssembly＋獨立 Web API 的執行計畫：已確認的決策、每個里程碑的範圍、固定流程、實機情境與進度。可行性、現況盤點與功能對照見 [WASM 改寫可行性分析報告](WASM改寫可行性分析報告.md)。

---

## 1. 進度

| # | 版本 | 內容 | 狀態 | 測試報告 |
|---|---|---|---|---|
| M0 | — | 可行性分析報告與本計畫（只動文件） | 完成 | — |
| M1 | 0.10.0 | 基礎建設 | 未開始 | — |
| M2 | 0.10.1 | 共用外框雙模式＋試點 `/Profile` | 未開始 | — |
| M3 | 0.10.2 | 分類、團隊、CRUD 產生器 | 未開始 | — |
| M4 | 0.10.3 | 專案、附件、下載票證 | 未開始 | — |
| M5 | 0.10.4 | 使用者、角色、稽核、郵件紀錄 | 未開始 | — |
| M6 | 0.10.5 | AI 串流＋日誌與 AI 管理頁 | 未開始 | — |
| M7 | 0.10.6 | 維運頁與首頁儀表板 | 未開始 | — |
| M8 | 0.10.7 | Agent 與系統管理助理＋通知推播 | 未開始 | — |
| M9 | 0.10.8 | 純 Token 登入 | 未開始 | — |
| M10 | 0.10.9 | 移除 Server 模式 | 未開始 | — |
| M11 | 0.10.10 | 拆成獨立靜態站台＋API 主機 | 未開始 | — |
| M12 | 0.10.11 | PWA 與離線 | 未開始 | — |

---

## 2. 已確認的決策

| 項目 | 決定 |
|---|---|
| Server 版 | 整個取代，最終不留 Server 渲染模式 |
| 部署網域 | 前後端同一主網域（app.公司.com＋api.公司.com，或同站台 /api） |
| 登入 | 最終純 JWT Token、不發 Cookie；理由：未來可能開發 App，API 要共用 |
| Token 存放 | 勾「記住我」→ localStorage；未勾 → sessionStorage |
| 過渡期登入 | M1～M8 沿用 Cookie 登入頁，WASM 頁以 Cookie 換 Token（`/auth/bridge`）後只用 Token 呼叫 API；M9 一次改純 Token |
| 離線程度 | 斷線不卡死即可，不做離線資料快取與同步 |
| 推進方式 | 漸進式，每個里程碑結束時整個系統完整可用 |
| 版本號 | M1 升 0.10.0，之後每個里程碑 Patch +1 |
| 測試報告 | 品質閘門＋Playwright 實機情境表＋截圖，每個里程碑一份 |
| AI 實測 | AI 相關里程碑用真實模型（User Secrets 金鑰，不印出） |

**實作端的決定**（理由見分析報告第 5 節）：專案名稱保留 `MyProject.Web`；新增伺服器端刷新 Token 紀錄表；資料存取統一用 Services；AI 串流用 NDJSON 串流 HTTP、不用 SignalR；通知以輪詢為準加串流推播；大檔用一次性下載票證；不做「我的登入裝置清單」頁。

---

## 3. 每個里程碑的固定流程

1. 開分支 `feature/wasm-mNN-<主題>`。
2. TDD：先寫測試、看到失敗，再實作。
3. `pwsh ./scripts/Invoke-QualityGate.ps1` 全部通過；動到 CRUD 樣板再跑 `scripts/Test-CrudGenerator.ps1`。
4. 實機：以 `scripts/Start-E2EHost.ps1` 啟動（覆寫 12 個 `ExternalFileSystem` 路徑、`NLog__BasePath`、以 `BootstrapSettings__SupportPassword` 給暫時密碼），用 playwright-core＋本機 Chrome 跑該里程碑情境，並把 37 個路由全部開過一次（煙霧測試），截圖。
5. 獨立程式審查。
6. 寫測試報告 `測試報告/M{nn}-<主題>.md`（A.1～A.7，見分析報告第 9 節）。
7. 文件同步：操作說明（`Datas/Help`）、新手完整操作指南、架構總覽、開發慣例速查、認證授權文件、API 端點目錄、changelog、索引與測試數。
8. `appsettings.json` 的 `SystemVersion` 依上表升版 → 回報並停下等指示（不自動 commit）。

---

## 4. 里程碑範圍

### M1 基礎建設（0.10.0）— 不搬頁面，UI 行為完全不變

- 新增 `MyProject.Web.Client`（只參考 Dtos／Models／Share），主機加上 WASM 渲染模式支援。
- `PermissionRules`、`AuthenticationCheckResult` 移到 Models 供前後端共用。
- `RefreshSession` 資料表＋migration；`/api/v1/auth/refresh` 改不透明 Token 輪替；新增 `/auth/logout`、`/auth/bridge`；`/auth/me` 回傳權限、團隊與強制改密碼／設定雙因素旗標。
- 抽出 `ICurrentUserLoader`；`ApiActorMiddleware` 依 JWT 每請求填 `CurrentUserService`；`ApiSessionGateFilter` 在強制改密碼／設定雙因素期間擋 API。
- Cookie 加登入工作階段 id，登出時一併撤銷對應刷新 Token。
- 刷新 Token 紀錄的保存期限排程與資料保留頁項目。
- 測試路徑統一（`SourceTree`）、E2E 工具（`scripts/e2e/`、`Start-E2EHost.ps1`）、複刻腳本試跑。
- **實機情境**：記住我有／無時 Token 落點；舊刷新 Token 重用被撤銷；改密碼後刷新被拒；登出撤銷；37 路由煙霧測試。

### M2 共用外框雙模式＋試點 `/Profile`（0.10.1）

- 外框（版面、側邊選單、通知鈴鐺、公告、操作說明）、共用元件、`theme.css`／`app.css`／`js` 移到 Client。
- 各區 `IXxxApi`：Client 用 HTTP 實作，Server 頁用行程內實作，讓外框在兩種模式都能渲染。
- 選單、說明、通知、公告、稽核歷程、頁面瀏覽稽核、前端日誌與錯誤回報、ui-config／ui-settings、個人資料 API。
- 跨模式導覽、AntDesign 繁中語系、前端遠端日誌。
- **實機情境**：Server 頁與 `/Profile` 往返、外框截圖比對、改姓名右上角同步、停掉伺服器時頁面不卡死、強制改密碼導向、通知測試出現在鈴鐺、登出後 Token 清空。

### M3 分類、團隊與 CRUD 產生器（0.10.2）

- Controller 改呼叫 Service，刪除 Category／Team Repository；補齊還原、啟用切換、批次刪除、匯出等 API。
- CRUD 樣板改產生 Client 頁面＋API 用戶端；`Test-CrudGenerator.ps1` 產生示範模組並實跑。
- **實機情境**：完整 CRUD、重名、兩分頁同時修改（409）、團隊範圍、唯讀角色看不到按鈕且 API 回 403、Excel／CSV（含 BOM）、還原與批次刪除、產生器示範模組。

### M4 專案、附件與下載票證（0.10.3）

- 專案 API 改呼叫 Service，刪除最後一個 Repository；附件上傳／刪除；一次性下載票證，退役 `ProjectFileController`。
- **實機情境**：多檔上傳、超大檔被拒、下載內容與上傳一致、票證重放與他人使用皆 404、非團隊成員無法下載、刪除專案後實體檔移除。

### M5 使用者、角色、稽核、郵件紀錄（0.10.4）

- 使用者（含複製、解鎖、強制登出、重設雙因素）、角色權限矩陣、稽核查詢／匯出／雜湊鏈驗證、郵件紀錄 API。
- `[AdminOnly]` 篩選器＋「每個 API action 都要標註授權」守門測試。
- **實機情境**：改他人角色後對方下次呼叫被登出、改自己角色仍維持登入、停用帳號刷新被拒、非管理員直打管理 API 得 403、匯出。

### M6 AI 串流＋日誌與 AI 管理頁（0.10.5）

- NDJSON 串流基礎（伺服器端 `NdjsonResult`、前端串流用戶端）、IIS 對串流關閉壓縮與緩衝。
- `/logs`、`/log-level-setting`、`/system-exceptions`、`/ai-call-logs`、`/token-usage`、`/prompt-templates`。
- **實機情境（真實模型）**：逐字出現、停止生成保留部分內容、關閉對話窗取消並在 AI 對話紀錄可見、例外追問、超過配額提示、`javascript:` 連結被中和、PDF／Excel 匯出。

### M7 維運頁與首頁儀表板（0.10.6）

- `/system-health`、`/database-usage`、`/scheduled-jobs`、`/backups`（票證下載，退役 `BackupController`）、`/data-retention`、`/system-parameters`、`/announcements`、`/App`。
- **實機情境**：立即執行排程看進度、建立並下載備份、修改參數生效、公告在另一分頁出現、首頁小工具依權限顯示。

### M8 Agent、系統管理助理與通知推播（0.10.7）

- Agent 對話每次請求接續已保存對話＋每段對話一把鎖；工作流程執行登錄表；通知推播串流。
- **實機情境（真實模型）**：寫入工具核准與拒絕、核准中重新整理可恢復、工作流程人工介入、兩分頁同時送出被排隊、巡檢報告連結、推播 2 秒內到達、關閉推播仍可輪詢。

### M9 純 Token 登入（0.10.8）

- 登入、雙因素、Google、忘記／重設密碼、變更密碼、雙因素設定、`/`、`/Error`、`/not-found` 搬到 Client；停發 Cookie、移除 bridge。
- 多分頁刷新排隊；sessionStorage 登入的新分頁向已開分頁取得 Token。
- **實機情境**：記住我有／無時關閉重開與開新分頁、連錯 5 次鎖定、雙因素＋記住裝置、強制改密碼無法直打 API 繞過、忘記密碼信件、Google 登入、Token 重用偵測、兩分頁同時刷新不被登出。

### M10 移除 Server 模式（0.10.9）

- 刪除 Server 渲染、電路處理、重連畫面、`AuthenticationStateHelper`、行程內轉接層、Cookie 驗證與相關類別。
- **實機情境**：37 路由全回歸、確認沒有 WebSocket／`_blazor` 連線。

### M11 獨立靜態站台＋API 主機（0.10.10）

- Client 改為獨立站台（`index.html`、`ApiBaseUrl`）；後端移除 Razor 元件、加 CORS 與 `ClientBaseUrl`（信件連結、Google 導回）；靜態站 web.config（SPA 回退、MIME、Brotli、CSP）。
- 複刻腳本處理兩個主機；IIS 雙站台與同站台反向代理兩種部署寫入文件。
- **實機情境**：雙主機啟動、非允許來源被 CORS 擋下、重設密碼信件連結指向前端、Google 導回前端、記錄冷啟動下載量與時間。

### M12 PWA 與離線（0.10.11）

- Service Worker、manifest、離線提示列、離線 API 訊息、新版本提示、修剪與延遲載入、最終文件整理。
- **實機情境**：離線時外框可開、提示出現、不卡死；恢復連線自動復原；部署新版出現提示；可安裝為 PWA。

---

## 5. 最終驗收

M12 完成後，依 [分析報告第 4 節功能對照表](WASM改寫可行性分析報告.md#4-功能對照表原功能--wasm-後如何保留) 逐項做全功能回歸，另做雙主機部署、離線與真實模型 AI 的實機驗證，結果寫入最終測試報告。
