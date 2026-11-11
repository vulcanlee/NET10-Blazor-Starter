# WASM 遷移計畫

- 文件版本：1.10
- 文件狀態：進行中
- 現行系統版本：0.10.9
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
| M8 | 0.10.7 | Agent 與系統管理助理＋通知推播 | 完成 | [M08-Agent系統管理助理與通知推播](測試報告/M08-Agent系統管理助理與通知推播.md) |
| M9 | 0.10.8 | 純 Token 登入 | 完成 | [M09-純Token登入](測試報告/M09-純Token登入.md) |
| M10 | 0.10.9 | 移除 Server 模式 | 完成 | [M10-移除Server模式](測試報告/M10-移除Server模式.md) |
| M11 | 0.10.10 | 拆成獨立靜態站台＋API 主機 | 未開始 | — |
| M12 | 0.10.11 | PWA 與離線 | 未開始 | — |

---

## 2. 已確認的決策

| 項目 | 決定 |
|---|---|
| Server 版 | 整個取代，最終不留 Server 渲染模式（0.10.9 已移除） |
| 部署網域 | 前後端同一主網域（app.公司.com＋api.公司.com，或同站台 /api） |
| 登入 | 最終純 JWT Token、不發 Cookie；理由：未來可能開發 App，API 要共用 |
| Token 存放 | 勾「記住我」→ localStorage；未勾 → sessionStorage |
| 過渡期登入 | M1～M8 沿用 Cookie 登入頁，WASM 頁以 Cookie 換 Token（`/auth/bridge`）後只用 Token 呼叫 API；M9 一次改純 Token（0.10.8 已完成：不發登入 Cookie、移除 bridge） |
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
4. 實機：以 `scripts/Start-E2EHost.ps1` 啟動（覆寫 12 個 `ExternalFileSystem` 路徑、`NLog__BasePath`、以 `BootstrapSettings__SupportPassword` 給暫時密碼），用 playwright-core＋本機 Chrome 跑該里程碑情境，並把全部路由開過一次（煙霧測試；0.10.7 以前 37 個、0.10.8 起移除 `/Auths/RefreshSession` 為 36 個），截圖。
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
> - **公告「廣播走通知機制」改成同分頁訊號＋輪詢**：伺服器存檔後照 Server 版清快取並廣播（還在 Server 的頁面即時更新）；WASM 收不到伺服器訊號，公告頁存檔或刪除後以新的 `ClientAnnouncementSignal` 請同一個分頁的橫幅立即重讀，其他分頁照舊 60 秒輪詢與切回分頁時重讀（M8 的通知推播串流再補即時；0.10.7 已完成）。
> - **外框立即重讀的共用基底 `ClientRefreshSignal`**：`ClientNotificationSignal`（系統健康「發送測試通知」、首頁點開通知後請鈴鐺立即重讀；鈴鐺標為已讀或刪除後以 `RequestRefresh(except:)` 請首頁「我的通知」重讀、跳過自己）與 `ClientAnnouncementSignal` 共用；只對同一個分頁有效。
> - **小修正：M2 留下的時差問題**：`NotificationsController`（鈴鐺）、`ProfileController` 與 `ShellController`（「關於」的啟動時間 `StartedAtLocal`）三處的本地時間原本帶 `+08:00` 送出（`ConvertTimeFromUtc`／`DateTimeOffset.LocalDateTime` 回 `Kind=Local`），伺服器與瀏覽器時區不同時鈴鐺、個人資料頁與「關於」的時間會錯；改為不帶時差，補測試 `NotificationsProfileAndShellInfo_ShouldSendLocalTimesWithoutAnOffset`（ShellController 是獨立審查發現）。
> - **獨立審查**：輪詢失敗不再每 2／5 秒疊一則不會自動關閉的錯誤通知（排程、備份、資料保留頁，改為只記日誌）；「我的通知」第一次讀取未完成就換頁時不再留下訂閱；「關於」的啟動時間時差；文件的續傳與 5 秒。另記兩項設計說明（備份票證是「持有連結即可下載」、公告 API 的起訖時間是伺服器本地時間），見 changelog 與測試報告。
> - **與 Server 版不同（使用者看得到的）**：備份下載不能續傳；排程執行中 2 秒自動更新；系統健康頁讀不到報告時停在「正在讀取…」並跳錯誤通知（Server 版整頁進錯誤畫面），資料庫用量頁讀不到時顯示「讀取資料庫用量失敗：…」（Server 版一直停在讀取中）；發送測試通知後只有這一頁的鈴鐺立即更新、其他已開的頁面最慢 60 秒（說明文字照抄 Server 版的「即時更新」，操作說明已補充）；「立即清理」稽核的 `expected` 由伺服器在按下時重算，可能和確認窗差幾筆，自訂清除的條件由伺服器驗證。
> - **守門測試跟著搬遷調整**：`BatchDeleteConventionTests` 改掃 Server 與 WASM 兩處畫面目錄（有單筆刪除的列表頁都搬到 WASM 了，只掃 Server 會空跑）；`PageAuthorizationTests` 呼叫 `Check()` 的元件掃描下限降為 4。
> - 實機腳本：`scripts/e2e/m07-views.mjs`（9 個畫面基準／比對；佈置固定公告與兩個空 zip 備份檔）、`m07-ops.mjs`（M7-P1～P9＋37 路由煙霧測試）。

### M8 Agent、系統管理助理與通知推播（0.10.7）

- Agent 對話每次請求接續已保存對話＋每段對話一把鎖；工作流程執行登錄表；通知推播串流。
- **實機情境（真實模型）**：寫入工具核准與拒絕、核准中重新整理可恢復、工作流程人工介入、兩分頁同時送出被排隊、巡檢報告連結、推播 2 秒內到達、關閉推播仍可輪詢。

> **M8 實際做法與計畫的差異**：
> - **Agent 對話照計畫「每個請求接續一次＋每段對話一把鎖」**：測試台與系統管理助理共用 `Controllers/AgentConversationEndpoint.cs`，每個請求以 `AiAgentRunner.ResumeAsync` 接續存檔、執行、存檔，伺服器不留對話物件，所以每個請求用當下系統參數的模型。鎖（`AiAgentConversationLocks`）拿不到時**排隊**而不是拒絕（使用者決定；Server 版沒有鎖、兩個分頁會互相覆蓋）：先回 200 串流並送心跳，輪到才驗證，錯誤放在 `result` 那一行。核准另以 `HasPendingApproval` 條件式更新搶占，跨行程也只執行一次。
> - **Server 版只在畫面擋的規則搬到伺服器**：訊息 2,000 字（400）、等待核准時送新訊息 409、核准編號必須在目前待核准裡（否則 409，同一核准送兩次只執行一次）、沒有待核准卻送核准 409、不可復原動作沒輸入「確認」400（`AdminAssistantApproval` 搬到 Client 共用）。
> - **系統管理助理核准後沿用原本分派到的領域**：每個請求都重新接續，原本會退回「全部工具＋全部領域」；分派結果代碼存進對話檔的 `TurnKey`，接續時 `IAiAgentTurnPlanner.RestoreTurnAsync` 重建同一輪（不呼叫分派模型）。
> - **網址帶 `?conversation={id}`**（計畫沒有，順手加上）：重新整理回到同一段對話，停在核准的再跳出核准窗。
> - **工作流程執行登錄表**：`AiWorkflowRunRegistry`（singleton）每次執行一個 `AsyncServiceScope`（填入目前使用者與來源 IP，每次回覆更新）、一人同時一次（第二個 409）、別人 404、閒置 30 分鐘結束成「中斷」。比計畫多的：**停在等人回覆時重新整理或關分頁，30 分鐘內回到頁面自動接回**（`GET runs/active`）；串流途中斷線仍中斷整個流程（同 Server 版）。已知限制：只在單一行程有效，IIS 回收或重啟後執行就沒了。
> - **通知推播串流**（計畫「通知以輪詢為準加串流推播」）：`POST api/v1/notifications/stream`（`[SignedInUser]`），`ready` → `notifications`／`announcements`（短時間多次合併）→ 閒置每 25 秒 `ping`；Token 到期前 1 分鐘送 `renew`、滿 30 分鐘或網站關閉送 `reconnect`。每人最多 3 條（第 4 條 429，保留 HTTP/1.1 下另外 3 條給一般 API）；開關 `NotificationSettings:PushEnabled`（`shell/info` 多回 `NotificationPushEnabled`）。WASM 的 `ClientPushConnection` 收到變動就觸發 M7 的 `ClientNotificationSignal`／`ClientAnnouncementSignal`，斷線 2→4→…→60 秒退避；60 秒輪詢保留為備援。沒做跨分頁共用一條連線（BroadcastChannel）：複雜度高，正式環境 IIS＋HTTPS 通常是 HTTP/2。
> - **共用的串流心跳 `NdjsonHeartbeat`**：資料源閒置時插 ping（Agent／工作流程 15 秒、推播 25 秒），避免代理伺服器把閒置連線切斷。⚠️ `NdjsonResult` 不會替無限長的串流停下來，推播的產生端自己看 `RequestAborted` 並取消訂閱；推播連線不記慢請求。
> - **與 Server 版不同（使用者看得到的）**：網址帶對話；兩個分頁排隊、核准只執行一次；工作流程等人回覆時重新整理可接回、一人一次；按「停止產生」後畫面自己補「已停止產生…」（M6 的做法），還在排隊就按停止則重讀伺服器上的對話；推論等級下一則訊息才在伺服器套用；每個請求用當下的模型；API 錯誤改成一般錯誤通知；鈴鐺與公告在其他分頁與其他使用者那邊通常幾秒內更新。
> - **獨立審查（5 項，全部修正）**：工作流程第二個分頁接回來看一下就離開會中斷第一個分頁的流程（改為只放棄自己開始或回覆過的、`onlyIfIdle`，409 時重讀）；對話內容檔同時讀寫時寫入可能失敗而卡死（改共用模式並重試，補 `AiAgentConversationFileStoreTests`）；排隊中按停止會補上伺服器沒存的紀錄（改為重讀）；核准後那一輪收不到「由誰回答」（每個請求至少送一次）；網站關閉時登錄表串流中途就釋放 scope（先中斷、等收尾最多 5 秒）。
> - **守門測試跟著搬遷調整**：`PageAuthorizationTests` 呼叫 `Check()` 的元件下限降為 1（只剩兩步驟驗證設定）；`AdminPatrolNotificationTests` 改讀 Client；`AdminPatrolReportsModalTests` 改寫為 WASM 版。
> - 實機腳本：`scripts/e2e/m08-views.mjs`（4 個初始畫面比對）、`m08-agents.mjs`（M8-G1～G9＋37 路由煙霧測試；`nopush` 參數跑 M8-N1，主機加 `NotificationSettings__PushEnabled=false`）。實機注意：真實模型常先回問「請確認是否要修改？」再呼叫寫入工具，腳本會回答「確認」。

### M9 純 Token 登入（0.10.8）

- 登入、雙因素、Google、忘記／重設密碼、變更密碼、雙因素設定、`/`、`/Error`、`/not-found` 搬到 Client；停發 Cookie、移除 bridge。
- 多分頁刷新排隊；sessionStorage 登入的新分頁向已開分頁取得 Token。
- **實機情境**：記住我有／無時關閉重開與開新分頁、連錯 5 次鎖定、雙因素＋記住裝置、強制改密碼無法直打 API 繞過、忘記密碼信件、Google 登入、Token 重用偵測、兩分頁同時刷新不被登出。

> **M9 實際做法與計畫的差異**：
> - **拆成 9a／9b 兩段、一次合併**：9a 先補 API 與基礎建設、搬 5 個中性頁（`/ChangePassword`、`/TwoFactorSetup`、`/`、`/Error`、`/not-found`），登入方式不變（仍發 Cookie、保留 bridge），先讓「新 API＋舊登入」整套可用；9b 才真正切換：搬登入相關 6 頁（`/Auths/Login`、`/Auths/TwoFactor`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`、`/Auths/Pending`、`/Auths/Logout`）、停發登入 Cookie、移除 bridge 與 `/Auths/RefreshSession`。兩段在同一個分支完成，只升一次版（0.10.8）。
> - **Cookie 驗證提前在 M9 移除**（計畫原本放 M10）：登入 Cookie 的 scheme、`SecurityStampCookieEvents`、`CookieClaims`、`SessionRefreshTicketService`／`SessionRefreshNavigator`、`auth-reset.js`、`ExpectedUserId`、待驗與記住裝置兩個 Cookie、`/UploadFiles` 靜態下載一併刪除——沒有任何流程發 Cookie，留著只會讓舊的 Cookie 還有地方被讀。預設驗證機制改 JWT；健康監控改檢查「JWT 是預設且設定完整」。只留 Google 登入過程的 5 分鐘外部 Cookie。
> - **效期的決定**：網頁的 Token 屬於發出管道 `web`，設定區段 `SessionSettings` 取代 `CookieSettings`（舊鍵仍相容、啟動時記警告），沿用 Server 版 Cookie 的語意——勾「記住我」→ localStorage、整串上限 30 天（從登入算、不滑動）；沒勾 → sessionStorage（關掉分頁就登出）、刷新 Token 閒置 480 分鐘（滑動；`IdleTimeoutSliding=false` 時為從登入起的絕對上限）；存取 Token 15 分鐘。App 的 API 登入照舊看 `JwtSettings`。存取 Token 帶這一串的識別（`fid`），每個請求檢查是否已撤銷：同一台主機登出立即生效，多台最多 30 秒。
> - **新分頁取得 Token**（計畫的「向已開分頁取得」）：BroadcastChannel 問已開分頁，對方**在刷新鎖內**以自己的刷新 Token 呼叫 `session/fork` 開一串新的交過來——每個分頁各一串，不會互相觸發重用偵測；1 秒沒人回應就回登入頁。
> - **同一個瀏覽器只有一個身分**（計畫沒寫，獨立審查補上）：沒有 Cookie 之後，兩個分頁可以各登入不同的人，新分頁可能向另一個人的分頁要到 Token。改成登入成功時廣播「誰登入了」，顯示另一個人的分頁丟掉自己的 Token、重新載入改用新的登入——與 Server 版 Cookie 的行為相同。`storage` 事件另處理別的分頁登出（回登入頁）與換人（整頁重新載入）。
> - **限流修正**（獨立審查）：限流原本排在驗證之前，「已登入每人」的分區實際上全部依 IP——同一個出口 IP 的人共用配額。改排在 `UseAuthentication` 之後（補 `PerUserRateLimitTests`）。嚴格登入配額擴大到 `two-factor`（含設定）、`google`、`password`、`session`，**網頁登入 `login/web` 與 `login-settings` 不算**（照 Server 版畫面登入靠帳號鎖定，算進每 IP 10 次會擋到同一個出口 IP 的公司早上登入尖峰）。
> - **Google 改一次性代碼**：回呼不發 Cookie，產生只存雜湊、60 秒、一次、綁定發起瀏覽器驗證值的代碼，導回 `/Auths/Login#code=` 由前端兌換；沒帶綁定值的舊流程一律回登入頁、不建立或連結帳號。兩步驟驗證的待驗票證、記住裝置 Token、設定票證改用 Data Protection（不同用途）加密，綁使用者與工作階段版本。
> - **頁面不再標 `[Authorize]`**：整頁請求只回空的 WASM 外殼，`ClientMainLayout` 是登入閘門；匿名白名單 9 頁。強制改密碼／設定兩步驟驗證時，外框要用的 API 標 `[AllowDuringForcedSetup]`，強制頁照 Server 版顯示完整外框；已在目標頁不再導頁（避免無限重新載入）。
> - **改到自己後不再換發 Cookie**：改密碼、啟用／停用兩步驟驗證、管理員改自己的 API 直接回這台的新 Token（`SelfSessionRenewal`），前端存起來後整頁重新載入；`PUT api/v1/users/{id}` 移除 `SessionRefreshUrl`。
> - **補上 M7 留下的缺口**：沒有主要角色的帳號，Server 版會登出；`/App` 搬到 WASM 後其實沒擋。9a 先寫測試證明 bridge 換出的 Token 原本可用，再讓網頁 Token 一律 403 `NO_PRIMARY_ROLE`、前端登出。
> - **獨立審查（7 項，全部修正）**：偷到的刷新 Token 先 fork 一串可躲過重用偵測（改為撤銷同一次網頁登入的每一串）；限流在驗證之前、網頁登入算進每 IP 10 次（見上）；舊設定 `CookieSettings:SlidingExpiration=false` 被靜默忽略而變成永不到期（新增 `IdleTimeoutSliding`）；同一瀏覽器兩個身分（見上）；fork 與登出同時發生時新串可能沒被撤銷（fork 後再確認來源）；不再寫 `Login.SessionExpired`（刷新因版本換掉而失敗時補寫）；匿名頁的瀏覽器錯誤回報觸發 401 被導回登入頁（沒登入時只留在主控台）。
> - **與 Server 版不同（使用者看得到的）**：沒勾「記住我」時關掉分頁就登出（Server 版要關掉整個瀏覽器），開新分頁會自動接續；管理員改自己、改密碼、啟用／停用兩步驟驗證後不再經過換發登入的過場頁；錯誤頁的追蹤碼改經網址帶過去（正式環境頁面出錯改 302 導到 `/Error?trace=`）。
> - 實機腳本：`scripts/e2e/m09-views.mjs`（10 個畫面基準／比對）、`m09-auth.mjs`（M9-T1～T11＋36 路由煙霧測試）；`common.mjs` 的 `login` 等 WASM 表單、`apiAsPageUser` 改從 storage 讀 Token；m01～m08 跟著改。`Start-E2EHost.ps1` 0.10.8 起信件一律存成 .eml 到隔離資料夾（開發機 User Secrets 可能設了真的 SMTP）。實機注意：WASM 頁面一直開著推播連線，腳本不可用 `networkidle`。

### M10 移除 Server 模式（0.10.9）

- 刪除 Server 渲染、電路處理、重連畫面、`AuthenticationStateHelper`、行程內轉接層、Cookie 驗證與相關類別（登入 Cookie 的 scheme 已在 M9 提前移除；剩下 `HasPermissionAttribute`／`RequestActorResolver` 的 Cookie 分支等）。
- **實機情境**：36 路由全回歸、確認沒有 WebSocket／`_blazor` 連線。

> **M10 實際做法與計畫的差異**：
> - **要刪的比計畫少**：登入 Cookie 的 scheme 與相關類別已在 M9 提前移除；「行程內轉接層」在 M2 就決定不做（外框改做 WASM 複本），所以沒有可刪的。M10 實際刪除：Server 版的 `Components/Routes.razor`、`ApplicationCircuitHandler`、`LoggingErrorBoundary`、`Components/Layout` 的 Server 外框（`MainLayout`、`NavMenu`、`NotificationBell`、`AnnouncementBanner`、`SidebarMenuNode`、`EmptyLayout`、`NoFooterLayout`、`ReconnectModal`）、`Components/Commons` 的 `PageHelpDialog`、`ReloadAsFullPage`、`ServerAuditHistorySource`、`Auth/ServerPagePermissions.cs`，以及 Business 的 `AuthenticationStateHelper`、`AuthenticationCheckResult`；`RequestActorResolver`、`HasPermissionAttribute` 的 Cookie 分支拿掉，只認 JWT（Google 登入過程的 5 分鐘外部 Cookie 保留）。
> - **主機只剩 API、WASM 主機頁與靜態檔**：`Program.cs` 拿掉 `AddInteractiveServerComponents`（含 hub 選項）、`AddInteractiveServerRenderMode`、`AddCascadingAuthenticationState`、伺服器端的 `AddAntDesign` 與 AntDesign 語系、`ViewNotification` 設定；`MyProject.Web.csproj` 移除 AntDesign、BlazorMaterialIcons 套件參考（靜態檔經 Client 專案參考照樣提供，`MyProject.Web.styles.css` 仍產生並匯入 Client 的 Scoped CSS）。`Components/App.razor` 一律渲染 `ClientRoutes`（`InteractiveWebAssemblyRenderMode(prerender: false)`，`HeadOutlet` 同），計畫的「獨立 `index.html`」留到 M11；找不到的網址照舊回 404 後重跑成 `/not-found`。慢請求排除清單拿掉 `/_blazor`。
> - **外框 API 還在用的伺服器類別暫留原資料夾**（M11 拆主機時再搬）：`SidebarMenuService`（選單過濾改用 `PermissionRules.CanAccessPage`，不再經 `AuthenticationStateHelper`）、`PageHelpService`、`PageHelpMarkdownParser`、`PageHelpDocument`、`HelpMarkdownRenderer`、`SidebarMenuItemModel`、`PageHelpTopicModel`、`AnnouncementCache`（公告挑選規則 `Select` 從已刪除的公告橫幅搬來）、`TextDownloadPayload`、`ViewAudit`；異動歷程的兩筆稽核寫法併入 `AuditLogsController`。
> - **暫留且已知沒有作用**（M11 整理）：`CurrentUserService.Changed`、`CircuitDisconnect`、`BrowserErrorReporter` 的 `[JSInvokable]`、`MyProject.Web.csproj` 的 `BlazorDisableThrowNavigationException`。`ClientErrorReporting:MaxPerCircuitPerMinute` 鍵名沿用舊名，實際是 `ShellController` 每位使用者每分鐘的上限。
> - **守門測試**：新增 `WasmOnlyHostTests`（`/_blazor/negotiate` 不成功、沒有 `CircuitHandler`、主機組件只剩 `App` 且沒有 `@page`、`MyProject.Web` 原始檔不提 Server 模式；先寫並確認 4 項失敗）；`AuthenticationStateHelperTests` 改寫為 `CurrentUserLoaderTests`；刪除 Server 鈴鐺與公告橫幅的 `NotificationComponentTests`；`SourceTree.UiRoots` 只剩 Client、新增 `UiFiles()`（排除 bin／obj），改掃 Client 的守門測試各自保留下限斷言（`TableDefaultSortConventionTests` 補上下限）；`FormModalConventionTests` 的 OverlayStyles／AntContainer 檢查改看 `ClientRoutes.razor` 與 `ClientErrorBoundary`；`PageAuthorizationTests` 拿掉 Server 的 `Check()` 與 `MainLayout` 檢查；bUnit 元件測試改用 `TestPagePermissions`。測試 3,050 → 3,040。
> - **與 Server 版不同（使用者看得到的）**：沒有，畫面與行為不變。不再有 Server 的重連提示畫面——WASM 斷線時是 API 錯誤通知（M2 起已是如此）。
> - 實機腳本：`scripts/e2e/m10-no-server.mjs`（N1 36 個路由沒有 WebSocket／`_blazor` 請求、N2 整頁請求只回 WASM 外殼、N3 `/_blazor/negotiate` 不存在、N4 都以 WebAssembly 執行，＋36 路由煙霧測試）；`smoke-routes.mjs` 的 `runSmoke` 多一個 `onContext` 選項（每個瀏覽器內容建立後呼叫，讓情境腳本監聽請求與 WebSocket）；`m06-views.mjs` 的 `fillPicker` 改成等日期面板打開後以鍵盤輸入並按面板的「確定」、開日誌頁先等第一次查詢回來（實機發現：AntDesign 含時間的日期欄位直接 fill＋Enter，同一頁第二個欄位會吃不進去）。

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
