# WASM 遷移計畫

- 文件版本：1.7
- 文件狀態：進行中
- 現行系統版本：0.10.6
- 首次實作版本：0.10.0
- 最後核對日期：2026/11/11

## 文件目的

把 Blazor Server 改寫為 Blazor WebAssembly＋獨立 Web API 的執行計畫：已確認的決策、每個里程碑的範圍、固定流程、實機情境與進度。可行性、現況盤點與功能對照見 [WASM 改寫可行性分析報告](WASM改寫可行性分析報告.md)。

---

## 1. 進度

| # | 版本 | 內容 | 狀態 | 測試報告 |
|---|---|---|---|---|
| M0 | — | 可行性分析報告與本計畫（只動文件） | 完成 | — |
| M1 | 0.10.0 | 基礎建設 | 完成 | [M01-基礎建設](測試報告/M01-基礎建設.md) |
| M2 | 0.10.1 | WASM 版外框＋試點 `/Profile` | 完成 | [M02-外框與個人資料](測試報告/M02-外框與個人資料.md) |
| M3 | 0.10.2 | 分類、團隊、CRUD 產生器 | 完成 | [M03-分類團隊與CRUD產生器](測試報告/M03-分類團隊與CRUD產生器.md) |
| M4 | 0.10.3 | 專案、附件、下載票證 | 完成 | [M04-專案附件與下載票證](測試報告/M04-專案附件與下載票證.md) |
| M5 | 0.10.4 | 使用者、角色、稽核、郵件紀錄 | 完成 | [M05-使用者角色稽核郵件紀錄](測試報告/M05-使用者角色稽核郵件紀錄.md) |
| M6 | 0.10.5 | AI 串流＋日誌與 AI 管理頁 | 完成 | [M06-AI串流日誌與AI管理頁](測試報告/M06-AI串流日誌與AI管理頁.md) |
| M7 | 0.10.6 | 維運頁與首頁儀表板 | 完成 | [M07-維運頁與首頁儀表板](測試報告/M07-維運頁與首頁儀表板.md) |
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

> **M1 實際做法與計畫的差異**：`PermissionRules`、`AuthenticationCheckResult` 移到 Models 延到 M2（M1 沒有前端程式用到）；
> 版本快取改以資料庫攔截器即時清除（使用者要求一併修正「強制登出最多延遲 5 分鐘」）；
> 依獨立審查加上刷新 Token 的 30 秒寬限期、bridge 換出的 Token 不比 Cookie 活得久、bridge 以資料庫比對 Cookie 版本。

### M2 WASM 版外框＋試點 `/Profile`（0.10.1）

> **M2 實際做法與計畫的差異**（使用者睡覺期間，依「保守做法、寫進報告」的指示決定）：
> - **外框沒有做成「同一份在兩種模式下都能跑」**，而是在 `MyProject.Web.Client` 另做一份 WASM 版（`Layout/Client*`），Server 版外框完全不動。
>   原因：雙模式要替約 10 個服務各做介面與兩套實作，並改到其他 36 個頁面共用的外框；複本不會影響那些頁面。代價是 M2～M9 有兩份外框要同步，M10 刪除 Server 版。
> - 只有純顯示、不依賴伺服器服務的元件搬到 Client 共用（命名空間不變）：`StatusPill`、`OverlayStyles`、`ConfirmDialog`、`LogoutConfirm` 等。
> - `PermissionRules`、`AuthenticationCheckResult` 移到 Models 再延到 M3（M2 的頁面不需要權限判斷，選單在伺服器端過濾）。
> - 前端遠端日誌（`RemoteLoggerProvider`）沒做：WASM 頁面的未處理例外與瀏覽器錯誤已經送回伺服器（`ClientErrorBoundary`、`ClientErrorReporter`），一般日誌留在瀏覽器主控台，M6 視需要再加。
> - 從 WASM 頁登出不呼叫 `/api/v1/Auth/logout`，改由網頁登出頁依登入工作階段撤銷 Token（兩邊都做會留兩筆登出稽核）；M9 純 Token 登入後改回呼叫 API。
> - 獨立審查的 7 項發現全部修正（見測試報告 A.5）：Token 綁定網頁登入的使用者、錯誤回報與開頁稽核以使用者為單位限流、編碼網址的重新載入迴圈、斷線時刷新不讓整頁出錯、跨模式開頁稽核只記一筆、開頁稽核只收 WASM 路徑、定期更新的資源釋放。

- **M1 留下的必辦**（來自 M1 獨立審查與 A.6）：
  - 前端刷新 Token 要讓多個分頁排隊（Web Locks），拿到 401 時先重讀 storage 看別的分頁是否已換到新 Token，避免互相觸發重用偵測。
  - 刷新失敗（含 bridge 換出的 Token 到了 Cookie 的上限）時，Cookie 仍有效就重新呼叫 bridge；Cookie 也失效才導回登入頁。
  - 從 WASM 頁登出時，先呼叫 `/api/v1/Auth/logout` 並清掉 localStorage／sessionStorage，再導向 `/Auths/Logout`。
  - `PermissionRules`、`AuthenticationCheckResult` 移到 Models（Client 共用）。

- 外框（版面、側邊選單、通知鈴鐺、公告、操作說明）、共用元件、`theme.css`／`app.css`／`js` 移到 Client。
- 各區 `IXxxApi`：Client 用 HTTP 實作，Server 頁用行程內實作，讓外框在兩種模式都能渲染。
- 選單、說明、通知、公告、稽核歷程、頁面瀏覽稽核、前端日誌與錯誤回報、ui-config／ui-settings、個人資料 API。
- 跨模式導覽、AntDesign 繁中語系、前端遠端日誌。
- **實機情境**：Server 頁與 `/Profile` 往返、外框截圖比對、改姓名右上角同步、停掉伺服器時頁面不卡死、強制改密碼導向、通知測試出現在鈴鐺、登出後 Token 清空。

### M3 分類、團隊與 CRUD 產生器（0.10.2）

- Controller 改呼叫 Service，刪除 Category／Team Repository；補齊還原、啟用切換、批次刪除、匯出等 API。
- CRUD 樣板改產生 Client 頁面＋API 用戶端；`Test-CrudGenerator.ps1` 產生示範模組並實跑。
- **實機情境**：完整 CRUD、重名、兩分頁同時修改（409）、團隊範圍、唯讀角色看不到按鈕且 API 回 403、Excel／CSV（含 BOM）、還原與批次刪除、產生器示範模組。

> **M3 實際做法與計畫的差異**：
> - **批次刪除沒有另做批次 API**：WASM 頁面沿用共用的 `BatchDelete`，逐筆呼叫單筆刪除 `DELETE {id}`（每頁的刪除規則與每筆一列的稽核都在單筆刪除裡，與 Server 版相同，速查表 §6.28）。
> - **稽核歷程 API 提前做**：資料頁每列的「異動歷程」對話窗搬到 Client 共用，WASM 頁需要資料來源，所以先加了只限管理員的 `GET /api/v1/audit-logs/history` 與 `/{id}/content`（`AuditLogsController`）；稽核紀錄頁本身的其他端點在 M5（0.10.4）補上。
> - **新增日期時間格式端點**：瀏覽器內建的 zh-TW 時間格式和伺服器不同（「上午9:00:00」對「上午 09:00:00」），而且在 WASM 設定目前文化不影響渲染。改由不需登入的 `GET /api/v1/shell/culture` 取伺服器格式（`ClientCulture`），WASM 清單的日期時間欄位一律明確指定格式；Client 專案載入完整地區資料（下載量約多 1～2 MB，M12 實測）。
> - **`PermissionRules` 移到 Models**（M1、M2 延後的項目）在這裡完成；另加 `PagePermissions` 讓 `RequirePermission` 等共用元件在兩種頁面都能判斷權限。M2 的 `ClientNotify` 拿掉，WASM 頁面改用與 Server 版相同的 `ViewNotification`。
> - **錯誤訊息改成與畫面相同**：分類、團隊的 API 改走服務後，錯誤訊息就是畫面那一句（使用者確認沒有外部程式依賴舊訊息）；網址、HTTP 狀態碼與欄位不變（團隊「還有下屬不可刪除」「改成已刪除團隊的名稱」仍回 400）。
> - **產生器實跑**：`Test-CrudGenerator.ps1` 在暫存 worktree 以 `Start-E2EHost.ps1` 啟動網站，用 `scripts/e2e/crud-generated.mjs` 對三個產生的頁面實際新增、修改、刪除、顯示已刪除、還原（需要 `PLAYWRIGHT_CORE_DIR`，`-SkipRealRun` 略過）。使用者決定：產生器只產生 WASM 頁面（沒有 Server 版選項），也不寫衍生專案舊 Server 版 CRUD 模組的轉換指南。
> - **WASM 檢視沒有另寫 bUnit 元件測試，改以實機情境（測試報告 C12）＋單元測試涵蓋**：WASM 檢視的主要風險在瀏覽器裡（AntDesign 的勾選欄、語系、整頁載入），bUnit 測不到。
> - 獨立審查的 7 項發現全部修正並補測試（見測試報告）：只有檢視權限的人能經 API 列出與匯出已刪除資料、WASM 匯出被拒時顯示「驗證失敗」、存檔前檢查端點會透露資料是否存在且只要檢視權限、追蹤碼可能過期、讀不到表單選項時沒有提示、團隊部門樹匯出忽略啟用狀態條件、產生器實跑時暫時密碼出現在命令列。

### M4 專案、附件與下載票證（0.10.3）

- 專案 API 改呼叫 Service，刪除最後一個 Repository；附件上傳／刪除；一次性下載票證，退役 `ProjectFileController`。
- **實機情境**：多檔上傳、超大檔被拒、下載內容與上傳一致、票證重放與他人使用皆 404、非團隊成員無法下載、刪除專案後實體檔移除。

> **M4 實際做法與計畫的差異**：
> - **上傳不另開端點**：`POST`／`PUT api/v1/Project` 在同一個網址以 `[Consumes]` 分成 `application/json`（不帶附件，與以前相同）與 `multipart/form-data`（`data`＋`files`＋`removedFileIds`）兩個 action，走與 Server 版相同的存檔流程，資料與附件同一次存檔。
> - **上傳不經 WASM 記憶體**：選的檔案留在瀏覽器（`browser-files.js`），.NET 只拿 id、檔名、大小，存檔時由 JS 組 `FormData` 直接送出（`ApiClient.SendFormAsync`）。1 GB 的附件讀進 WASM 會吃光記憶體；實測 200 MB 上傳＋存檔 5.2 秒，頁面 JS 記憶體 8 MB。IIS 端新增 `web.config` 把請求上限放到 4 GB（`WebConfigTests` 守門）。
> - **下載不支援續傳**：票證只能用一次，下載中斷要回頁面重新點檔名（以前的 Cookie 下載端點支援續傳；使用者接受這個取捨）。
> - **使用者決定的行為統一**：專案標題改為不能重複（新增、改名與還原時擋，舊的同名資料不改標題仍可存；之前只有 API 擋）；API 的開始、結束日期改為可不填；API 的關鍵字比對、狀態／優先級／日期規則與錯誤訊息改成與畫面相同。
> - **下載用 `<a download>` 而不是 `location.assign`**：實機發現 `location.assign` 會開始一次頁面導覽，之後 AntDesign 表格出錯（切到「顯示已刪除」整頁空白），改用暫時的 `<a download>` 連結。
> - **票證存在記憶體、只在單一行程有效**：沒有另做資料表；IIS 應用程式集區要維持一個背景工作處理序（部署清單已加檢查項目），集區回收只會讓還沒用的票證失效。
> - **獨立審查後的調整**：資料與附件改在同一個資料庫交易存檔（附件失敗整筆不存、版本號不變可直接重存；之前專案會存進去、附件與稽核沒有）；標題存檔時去掉前後空白；還原也擋同名（原本決定不擋，審查後改與分類相同）；WASM 選檔時就檢查附件類型；JSON 版恢復接受 `text/json`、`application/*+json`，multipart 版設 `Order = 1`（沒帶 Content-Type 回 415 而不是 500）並不列入 Swagger；API 的已刪除清單套用分類／團隊篩選。已知限制寫進文件：標題唯一沒有資料庫索引（同時新增可能都通過）、全系統唯一的 409 會透露看不到的團隊有同名專案、下載管理員預先抓取會用掉票證。
> - **`RequestActorResolver` 保留**：Repository 都刪了，但 `RecordAccessScopeProvider` 在 API 路徑仍用它取使用者 Id（Controller 的稽核擴充與 HTTP 日誌也用），不能跟著刪。

### M5 使用者、角色、稽核、郵件紀錄（0.10.4）

- 使用者（含複製、解鎖、強制登出、重設雙因素）、角色權限矩陣、稽核查詢／匯出／雜湊鏈驗證、郵件紀錄 API。
- `[AdminOnly]` 篩選器＋「每個 API action 都要標註授權」守門測試。
- **實機情境**：改他人角色後對方下次呼叫被登出、改自己角色仍維持登入、停用帳號刷新被拒、非管理員直打管理 API 得 403、匯出。

> **M5 實際做法與計畫的差異**：
> - **授權意圖分三種標註＋守門測試**：除了 `[AdminOnly]`（`MyProject.Web/Filters/AdminOnlyAttribute.cs`；非管理員回 403 並寫 `Permission.Denied`，detail「需要管理員」），另加沒有行為的 `[SignedInUser]`，標在「登入即可、刻意不檢查頁面權限」的端點（個人資料、外框、通知、公告、`/me`），讓 `ApiAuthorizationConventionTests` 分得出「忘了標」和「本來就不需要」：每個 API action 都必須標 `[HasPermission]`、`[AdminOnly]`、`[SignedInUser]` 或 `[AllowAnonymous]`。
> - **備份下載留到 M7**：`BackupController.Download` 是 Cookie 驗證，不經 `ApiActorMiddleware`，`[AdminOnly]` 讀不到目前使用者，所以暫時標 `[SignedInUser]`、管理員檢查留在方法裡；M7 改成下載票證時改用 `[AdminOnly]`（0.10.6 已完成：`BackupController` 刪除，改為 `[AdminOnly]` 的 `POST api/v1/backups/{檔名}/ticket`）。
> - **改自己用伺服器回的網址換發登入**：`PUT api/v1/users/{id}` 改到自己而且工作階段版本換掉（改了自己的密碼、角色或管理員身分）時回 `SessionRefreshUrl`（`/Auths/RefreshSession?ticket=…&returnUrl=%2Fmyusers`，沿用 0.9.103 的一次性、2 分鐘 ticket），WASM 頁整頁導過去，這台裝置維持登入、其他裝置登出，與 Server 版相同。改別人、停用、強制登出、重設兩步驟驗證換對方的版本，對方下一個請求就被登出（API 401、刷新被拒）。
> - **自我保護補到 API**：Server 版「不可強制登出自己」「不可重設自己的兩步驟驗證」只在畫面擋，0.10.4 起 API 也擋（400）。停用、刪除自己與 support 的保護本來就在服務層。`MyUserService`、`RoleViewService`、`TwoFactorService` 的失敗都標上 `Kind`，API 回 404／409／400／500。
> - **時區由伺服器算**：使用者清單的「鎖定至 HH:mm」（`LockedText`）、稽核明細的 UTC 時間由伺服器依伺服器時區算好送來，瀏覽器時區不同也不會顯示錯。實機發現：稽核單筆查詢回的是 UTC，曾被當成本地再換一次，差 8 小時，已修正。獨立審查再發現：本地時間帶著 `+08:00` 送出時 WASM 會換成瀏覽器時區，改成一律不帶時差送出（稽核紀錄、異動歷程、寄信紀錄），UTC 以 `Z` 結尾；WASM 的日期篩選也先去掉時區（`ClientCulture.Unzoned`）。
> - **角色矩陣在 API 上是樹狀 DTO**（群組 → 頁面 → 動作，`RolePermissionDto`），由 `RoleDtoMapper` 與畫面模型互轉，伺服器再轉成權限鍵，規則不變；角色清單的搜尋每列含矩陣，「複製」直接用它。審查後只採納權限目錄裡的節點（呼叫端不能多塞管理員專屬鍵），修改不再覆寫建立時間。
> - **獨立審查後的其他調整**：support 不可改帳號（服務層，「「support」是系統預設的開發帳號，不可修改帳號。」；之前先改名就能繞過不可停用、不可刪除，改寫前就有的問題）；四個 controller 加 `[ApiValidationFilter]`（每頁 1～200 筆、頁碼 ≥ 1），帳號、名稱、角色與角色名稱的必填由伺服器再驗；產生密碼的回應 `Cache-Control: no-store`；新增 `POST api/v1/users/check`，「複製」與 Server 版同順序先檢查帳號再問 Email 重複；稽核匯出的稽核記實際筆數，被截掉時另記總筆數；守門測試加上「除了 `[AllowAnonymous]` 都要有 `[Authorize]`」並檢查繼承來的 action。
> - **稽核完整性驗證是長時間請求**：時間與紀錄筆數成正比，WASM 端改用 `ApiClient.PostLongRunningAsync`（最長 30 分鐘），不受一般 100 秒逾時限制。稽核 CSV 的欄位從頁面搬到 `MyProject.Web/Export/AuditLogExport.cs`。
> - **稽核頁「發生時間」排序**：Server 版的排序其實沒生效（AntDesign 給的方向字串是 `Ascending`／`Descending`，程式比對的是 `ascend`／`descend`），WASM 版依點選方向排序（小修正）。
> - 外觀：與 Server 版截圖比對 8 個畫面，4 個完全相同，其餘最多 17/255 的色差、肉眼無差異（`scripts/e2e/m05-views.mjs`；隔離資料庫裡 support 的 Email 改成 `support@e2e.test`，避免截圖露出個資）。

### M6 AI 串流＋日誌與 AI 管理頁（0.10.5）

- NDJSON 串流基礎（伺服器端 `NdjsonResult`、前端串流用戶端）、IIS 對串流關閉壓縮與緩衝。
- `/logs`、`/log-level-setting`、`/system-exceptions`、`/ai-call-logs`、`/token-usage`、`/prompt-templates`。
- **實機情境（真實模型）**：逐字出現、停止生成保留部分內容、關閉對話窗取消並在 AI 對話紀錄可見、例外追問、超過配額提示、`javascript:` 連結被中和、PDF／Excel 匯出。

> **M6 實際做法與計畫的差異**：
> - **串流格式**：每行一個 `AiStreamFrameDto`（`MyProject.Dtos/Ai/AiStreamDtos.cs`），`Kind` 依序是 `start`（帶範圍 `AiStreamScopeDto`）、`delta`（文字）、最後一定是 `result`（`AiResultDto`）。伺服器 `NdjsonResult`（`application/x-ndjson`、關閉回應緩衝、每行 flush、`Cache-Control: no-cache, no-transform`、`X-Accel-Buffering: no`）；WASM `ApiClient.StreamAsync<T>`（帶 Token、401 刷新後重送一次、強制設定的 403 整頁導走、被拒時回一般 `ApiResult`、只有標頭受 100 秒逾時、取消回失敗不丟例外）。
> - **使用者中斷後序列照樣讀完**：停止產生或關窗＝中斷請求，伺服器 `RequestAborted` 取消上游 AI 呼叫；`NdjsonResult` 不再寫出但把序列讀完，收尾的稽核與用量記帳一定執行。直接離開頁面造成的取消也會記 AI 分析稽核（Server 版因電路已收掉而不記）。
> - **停止產生後畫面自己收尾**：收不到伺服器最後那筆結果，用已收到的文字加「已中止」註記收尾，所以停止後對話窗資訊不顯示模型與用量（伺服器的稽核與用量紀錄仍完整；使用者確認接受）。
> - **例外 AI 對話不存伺服器快取**：[可行性分析報告](WASM改寫可行性分析報告.md)原寫「對話存伺服器快取」，改為畫面送 `ConversationId` 與例外明細之後的問答，伺服器以資料庫與堆疊檔重建第一則例外明細與 system 提示，並檢查角色交替、追問 ≤ 2,000 字、回覆 ≤ 512K 字、輪數 ≤ 上限。伺服器無狀態、不怕 IIS 回收，畫面也改不到例外內容與系統提示。例外 PDF 匯出不受追問輪數上限限制（對話途中調低上限仍可匯出）。
> - **IIS**：`application/x-ndjson` 不在動態壓縮的預設類型內，不必改 `web.config` 的設定；只加註解說明主機自訂了 `dynamicTypes` 或前面有會緩衝的反向代理時要排除（部署清單同步）。
> - **共用型別搬到 Client**（命名空間 `MyProject.Web.Ai` 不變）：`AiMarkdownRenderer`（Client 參考 Markdig）、`AiInterruption`、`AiTokenUsage`、`TokenUsageTrendChart`、`TokenUsageReportScope(s)`、`AiCallLogText`；`TokenUsageFormat`、`LogLevels.cs` 在 `MyProject.Web.Client/Diagnostics`；`ExceptionLogClipboardText` 命名空間改為 `MyProject.Web.Client.Views.Admins`。提示詞分頁標題搬到伺服器的 `PromptTemplateTitles`。CSV 欄位定義搬到 `MyProject.Web/Export/`（`ExceptionLogExport`、`AiCallLogExport`、`TokenUsageExport`）。
> - **「刪除選取」沒有批次端點**：照 M5 由畫面逐筆呼叫 `DELETE {id}`（每筆一列稽核）。
> - **小修正（與 Server 版不同）**：系統例外（最後發生、次數）、AI 對話紀錄（送出時間、耗時、送出字元）、Token 用量明細（時間、費用）的排序依點選方向生效，匯出、複製、PDF 與畫面同序（Server 版比對 `ascend`／`descend` 從未生效）；Token 用量 PDF 由伺服器依同條件重查（查詢與匯出之間有新紀錄時數字可能多幾筆）；例外「複製目前查詢結果」多一次查總筆數的請求，AI 是否可用改在開頁與重新整理時讀。
> - **實機發現：被限流就被登出**：煙霧測試一分鐘內整頁載入二十幾個 WASM 頁，被 API 限流（429）後 WASM 一路試到「重新登入」把人丟回登入頁（M2 就有的行為）。改為 `ClientSession.IsThrottled`：不清 Token、不回登入頁，外框顯示「請求過於頻繁，請稍後再試。」與「重新整理」；`ApiClient` 刷新被限流時回 429 訊息。`smoke-routes.mjs` 把「被丟回登入頁」與「停在限流提示」算失敗；`Start-E2EHost.ps1` 的測試主機把 API 限流放寬到每分鐘 1000 次。
> - **實機發現：提示詞頁沒有頁籤被選中**：AntDesign `Tabs` 先以零個頁籤畫出、資料到了才補頁籤時 `ActiveKey` 不套用，改成資料到了才畫 `Tabs`。
> - **獨立審查 7 項（修 6 項，1 項記為已知）**：Markdown 消毒漏掉角括號自動連結 `<javascript:…>`（高；main 就有）→ `AutolinkInline` 套同一份白名單、不合格降級成純文字；日誌 AI PDF 的報告與模型名稱沒有上限 → 加長度上限、超過回 400；按停止剛好收到失敗結果時對話卡住 → 閘門改看「關窗」；AI 對話紀錄內文掃描失敗後搜尋稽核記錯 → 稽核移到掃描前；讀取提示詞時取消被當成未預期錯誤、例外端點沒有防護 → 新增 `AiStreamGuard`（保證一定有結果）；版本號與 `web.config` 註解。資源用量（Token 用量 CSV／PDF 明細全表、例外複製最多 5000 筆含堆疊、AI 原文 JSON 一次讀進記憶體）與 Server 版相同，記為已知限制。
> - 實機腳本：`scripts/e2e/m06-views.mjs`（9 個畫面基準／比對）、`m06-ai.mjs`（真實模型 9 個情境；`quota` 參數另跑超過用量上限，主機加 `AiQuotaSettings__PerUserDailyTwd=1`）、`m06-ops.mjs`（操作 8 個情境＋37 路由煙霧測試）。

### M7 維運頁與首頁儀表板（0.10.6）

- `/system-health`、`/database-usage`、`/scheduled-jobs`、`/backups`（票證下載，退役 `BackupController`）、`/data-retention`、`/system-parameters`、`/announcements`、`/App`。
- **實機情境**：立即執行排程看進度、建立並下載備份、修改參數生效、公告在另一分頁出現、首頁小工具依權限顯示。

> **M7 實際做法與計畫的差異**：
> - **八頁都是只限管理員的 API（首頁除外）**：`ScheduledJobsController`、`SystemHealthController`、`BackupsController`、`DataRetentionController`、`DatabaseUsageController`、`SystemParametersController`，以及 `AnnouncementsController` 補上的管理端點，一律 `[AdminOnly]`；首頁的 `DashboardController` 是頁面 view 權限、排程作業小工具的端點 `[AdminOnly]`。稽核動作與內容照 Server 版、改由伺服器寫；本地時間以不帶時差送出，「現在」「今天」用伺服器時間。系統健康監控原本整頁寫在 `SystemHealthPage.razor`，拆成頁面＋`Views/Admins/SystemHealthView`。
> - **背景作業照舊交給排程佇列，畫面輪詢**：「立即執行」「立即備份」「立即清理」都走 `ScheduledJobOverviewService.TryTrigger`（已在佇列或執行中 409），請求馬上回來。排程頁有作業排隊或執行中時每 **2 秒**輪詢（計畫寫 1～2 秒；Server 版 5 秒），結束或離開頁面就停；備份與資料保留頁維持 5 秒。
> - **備份下載用 M4 的同一套票證**：`DownloadTicketService` 的目標改成字串（附件 Id 或備份檔名）、新增 `BackupKind`；兌換時多一道「仍是管理員」，檔名經 `BackupStore.TryResolveFullPath`，稽核 `Backup.Download` 在兌換時寫。`BackupController` 刪除，0.10.6 起已沒有以 Cookie 驗證的 API。與 M4 相同的取捨：**不能續傳**（Server 版的 Cookie 下載可以；計畫已拍板一次性票證）。
> - **首頁小工具由伺服器決定**：計畫寫「各小工具資料 API，依權限顯示」，實作是 `GET api/v1/dashboard` 依 `DashboardWidgetCatalog.VisibleTo` 只回看得到的小工具（快速入口也由伺服器過濾），Client 的 `DashboardWidgetComponents` 以代號對應元件、兩邊登記由 `DashboardTests` 檢查一致；每個小工具繼承 `DashboardWidgetBase` 自己讀資料，讀不到只影響那張卡片。伺服器的登記不再帶元件型別（`AddDashboardWidget("代號", …)`）。
> - **系統參數的驗證全在伺服器**：WASM 送 `Key`、`Value`、`ExpectedStamp`（鍵含冒號，放本文），`SystemParametersController` 走同一個 `SystemParameterManager`；不在目錄的鍵（例如 `JwtSettings:SigningKey`）404、不合法 400、版本衝突或設定檔有誤 409。`SystemParameterValueCodec` 搬到 Client 共用（`ISystemParameterFormat`），`SystemParameterKind`／`Source` 搬到 Dtos，`AnnouncementTargetKinds` 搬到 Share（命名空間不變）。
> - **公告「廣播走通知機制」改成同分頁訊號＋輪詢**：伺服器存檔後照 Server 版清快取並廣播（還在 Server 的頁面即時更新）；WASM 收不到伺服器訊號，公告頁存檔或刪除後以新的 `ClientAnnouncementSignal` 請同一個分頁的橫幅立即重讀，其他分頁照舊 60 秒輪詢與切回分頁時重讀（M8 的通知推播串流再補即時）。
> - **外框立即重讀的共用基底 `ClientRefreshSignal`**：`ClientNotificationSignal`（系統健康「發送測試通知」、首頁點開通知後請鈴鐺立即重讀；鈴鐺標為已讀或刪除後以 `RequestRefresh(except:)` 請首頁「我的通知」重讀、跳過自己）與 `ClientAnnouncementSignal` 共用；只對同一個分頁有效。
> - **小修正：M2 留下的時差問題**：`NotificationsController`（鈴鐺）、`ProfileController` 與 `ShellController`（「關於」的啟動時間 `StartedAtLocal`）三處的本地時間原本帶 `+08:00` 送出（`ConvertTimeFromUtc`／`DateTimeOffset.LocalDateTime` 回 `Kind=Local`），伺服器與瀏覽器時區不同時鈴鐺、個人資料頁與「關於」的時間會錯；改為不帶時差，補測試 `NotificationsProfileAndShellInfo_ShouldSendLocalTimesWithoutAnOffset`（ShellController 是獨立審查發現）。
> - **獨立審查**：輪詢失敗不再每 2／5 秒疊一則不會自動關閉的錯誤通知（排程、備份、資料保留頁，改為只記日誌）；「我的通知」第一次讀取未完成就換頁時不再留下訂閱；「關於」的啟動時間時差；文件的續傳與 5 秒。另記兩項設計說明（備份票證是「持有連結即可下載」、公告 API 的起訖時間是伺服器本地時間），見 changelog 與測試報告。
> - **與 Server 版不同（使用者看得到的）**：備份下載不能續傳；排程執行中 2 秒自動更新；系統健康頁讀不到報告時停在「正在讀取…」並跳錯誤通知（Server 版整頁進錯誤畫面），資料庫用量頁讀不到時顯示「讀取資料庫用量失敗：…」（Server 版一直停在讀取中）；發送測試通知後只有這一頁的鈴鐺立即更新、其他已開的頁面最慢 60 秒（說明文字照抄 Server 版的「即時更新」，操作說明已補充）；「立即清理」稽核的 `expected` 由伺服器在按下時重算，可能和確認窗差幾筆，自訂清除的條件由伺服器驗證。
> - **守門測試跟著搬遷調整**：`BatchDeleteConventionTests` 改掃 Server 與 WASM 兩處畫面目錄（有單筆刪除的列表頁都搬到 WASM 了，只掃 Server 會空跑）；`PageAuthorizationTests` 呼叫 `Check()` 的元件掃描下限降為 4。
> - 實機腳本：`scripts/e2e/m07-views.mjs`（9 個畫面基準／比對；佈置固定公告與兩個空 zip 備份檔）、`m07-ops.mjs`（M7-P1～P9＋37 路由煙霧測試）。

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
