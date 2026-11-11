# WASM 遷移 M10：移除 Server 模式（0.10.9）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.10.9
- 首次實作版本：0.10.9
- 最後核對日期：2026/11/11

## 目的

WASM 遷移的第十個里程碑（計畫見 [WASM 遷移計畫](../migration/WASM遷移計畫.md)）：M9 之後 36 個頁面已全部以 WebAssembly 執行，這次**移除 Blazor Server 互動模式**——電路、SignalR `/_blazor` 連線、重連畫面、Server 版的路由與外框、`AuthenticationStateHelper` 與只給它們用的類別全部刪除。
`MyProject.Web` 只剩 Web API 主機、WASM 的主機頁（`Components/App.razor`）與靜態檔。**畫面與 API 不變**；M11 再把前端拆成獨立靜態站台。
測試報告見 [M10-移除Server模式](../migration/測試報告/M10-移除Server模式.md)。

## 變更內容

### 1. 主機只剩 WebAssembly

- `Program.cs` 拿掉 `AddInteractiveServerComponents`（含 SignalR hub 選項）、`AddInteractiveServerRenderMode`、`AddCascadingAuthenticationState`、伺服器端的 `AddAntDesign` 與 AntDesign 語系設定、伺服器端的 `ViewNotification` 設定；沒有 `/_blazor` 端點。
- `Components/App.razor` 一律渲染 `MyProject.Web.Client.ClientRoutes`（`InteractiveWebAssemblyRenderMode(prerender: false)`，`HeadOutlet` 同一個模式），移除 `Routes` 分支、`ReconnectModal` 與判斷頁面屬於哪一邊的程式。找不到的網址照舊回 404 後重跑成 `/not-found`，由 WASM 顯示。
- `MyProject.Web.csproj` 移除 AntDesign、BlazorMaterialIcons 套件參考（靜態檔經 Client 專案參考照樣提供；`MyProject.Web.styles.css` 仍產生並匯入 Client 的 Scoped CSS）。
- 慢請求排除清單拿掉 `/_blazor`（只剩通知推播串流）。

### 2. 刪除的 Server 版程式

- `Components/Routes.razor`、`ApplicationCircuitHandler`、`LoggingErrorBoundary`。
- `Components/Layout` 的 Server 外框：`MainLayout`、`NavMenu`、`NotificationBell`、`AnnouncementBanner`、`ReconnectModal`、`EmptyLayout`、`NoFooterLayout`、`SidebarMenuNode`（WASM 版 `ClientMainLayout`、`ClientNavMenu`、`ClientNotificationBell`、`ClientAnnouncementBanner` 等 0.10.1 起已在使用）。
- `Components/Commons` 的 `PageHelpDialog`、`ReloadAsFullPage`、`ServerAuditHistorySource`；`Auth/ServerPagePermissions.cs`。
- Business 的 `AuthenticationStateHelper`、`AuthenticationCheckResult`（目前使用者的載入規則只剩 `ICurrentUserLoader`，由 API 每個請求使用）。

### 3. 保留在原資料夾的伺服器類別（M11 再搬）

外框與操作說明的 API 還在用：`SidebarMenuService`（選單過濾改用 `PermissionRules.CanAccessPage`，不再經 `AuthenticationStateHelper`）、`PageHelpService`、`PageHelpMarkdownParser`、`PageHelpDocument`、`HelpMarkdownRenderer`、`SidebarMenuItemModel`、`PageHelpTopicModel`、`AnnouncementCache`（公告挑選規則 `Select` 從已刪除的公告橫幅搬來）、`TextDownloadPayload`、`ViewAudit`。異動歷程的兩筆稽核寫法併入 `AuditLogsController`。

### 4. 身分只認 JWT

- `RequestActorResolver`、`HasPermissionAttribute` 拿掉登入 Cookie 的分支（0.10.8 起已不發登入 Cookie）：`NameIdentifier` 不是正整數的身分一律當成解析不到，絕不會把 `ClaimTypes.Name`（Cookie 時代是姓名）當帳號記下。Google 登入過程的 5 分鐘外部 Cookie 保留。
- 稽核的來源 IP：API 取 `HttpContext`；跨請求的工作流程執行（`AiWorkflowRunRegistry`）每次回覆時寫入。錯誤追蹤碼只剩「每個 HTTP 請求」與「每次排程作業」兩種來源。

### 5. 暫留且已知沒有作用（M11 整理）

`CurrentUserService.Changed`、`CircuitDisconnect`、`BrowserErrorReporter` 的 `[JSInvokable]` 與每實例計數、`MyProject.Web.csproj` 的 `BlazorDisableThrowNavigationException`。`ClientErrorReporting:MaxPerCircuitPerMinute` 鍵名沿用舊名，實際是 `ShellController` 每位使用者每分鐘的上限。

### 6. 與 Server 版不同、使用者看得到的地方

沒有，畫面與行為不變。不再有 Server 的重連提示畫面——WASM 斷線時是 API 錯誤通知（M2 起已是如此）。

### 7. 守門測試調整

- 新增 `WasmOnlyHostTests`（先寫並確認 4 項失敗）：`/_blazor/negotiate` 不成功、沒有註冊 `CircuitHandler`、主機組件只剩 `App` 一個元件且沒有 `@page`、`MyProject.Web` 的原始檔（含註解）不提 Server 模式（`InteractiveServer`、`CircuitHandler`、`ReconnectModal`、`AuthenticationStateHelper`、`AuthorizeRouteView`）。
- `AuthenticationStateHelperTests` 改寫為 `CurrentUserLoaderTests`；刪除 Server 鈴鐺與公告橫幅的元件測試 `NotificationComponentTests`；bUnit 元件測試改用 `TestPagePermissions`。
- `SourceTree.UiRoots` 只剩 `MyProject.Web.Client`，新增 `UiFiles(pattern)`（排除 `bin`、`obj`）；改掃 Client 的守門測試各自保留下限斷言（`TableDefaultSortConventionTests` 補上下限）。
- `FormModalConventionTests` 的 OverlayStyles 唯一渲染點與 AntContainer 錯誤邊界改看 `ClientRoutes.razor` 與 `ClientErrorBoundary`；`PageAuthorizationTests` 拿掉 Server 的 `Check()` 與 `MainLayout` 檢查；`MenuPermissionConsistencyTests` 只認 `Permissions.CanAccessPage`／`Permissions.IsAdmin`。

### 8. 測試

- 測試 3,050 → 3,040。
- 實機驗證腳本：`scripts/e2e/m10-no-server.mjs`（N1 36 個路由沒有 WebSocket／`_blazor` 請求、N2 整頁請求只回 WASM 外殼、N3 `/_blazor/negotiate` 不存在、N4 都以 WebAssembly 執行，＋36 路由煙霧測試）；`smoke-routes.mjs` 的 `runSmoke` 多一個 `onContext` 選項（每個瀏覽器內容建立後呼叫，讓情境腳本監聽請求與 WebSocket）。
- `m06-views.mjs` 的 `fillPicker` 改成等日期面板打開後以鍵盤輸入並按面板的「確定」、開日誌頁先等第一次查詢回來（實機發現：AntDesign 含時間的日期欄位直接 fill＋Enter，同一頁第二個欄位會吃不進去）。

### 9. 文件同步

開發慣例速查（§6.33 新增「移除 Server 模式」一節，外框、權限、日誌、稽核來源 IP 等改成現況）、架構總覽、Web API 端點目錄、介面視覺設計規範、對話窗 UI 設計規範、認證授權與權限機制、密碼種類與儲存機制、日誌與設定檔說明、正式部署與安全檢查清單、維護規範、測試指南、畫面與欄位字典、腳手架開發指引、建立新 CRUD 頁、VS Code 指南、腳手架總覽、AI 日誌分析、相關 PRD（元件名改成 `Client*`）、遷移計畫與可行性分析報告、readme、CLAUDE.md／AGENTS.md。另更正錯誤通知「複製」附完整例外的說明：只有管理員可以（0.10.8 起頁面全部在 WASM 即如此），`Security:ReturnExceptionDetails` 只決定 API 500 回應是否帶例外細節。

## 升版注意

- 版本號 0.10.8 → 0.10.9。
- 沒有資料庫異動、沒有設定鍵異動。
- 衍生專案若在 `MyProject.Web` 加過畫面元件、頁面或 Server 外框的修改：搬到 `MyProject.Web.Client`（`Layout/Client*`、`Pages`、`Views`），資料改經 Web API；`WasmOnlyHostTests` 會擋下留在主機的元件與 `@page`。
- 衍生專案若有程式依賴 `AuthenticationStateHelper`、`AuthenticationCheckResult`、`ServerPagePermissions`、`ServerAuditHistorySource`：改用 `ICurrentUserLoader`、`PermissionRules`、WASM 的 `PagePermissions`／`ApiAuditHistorySource`。
- 衍生專案若有用登入 Cookie 身分呼叫 `[HasPermission]` 端點的用戶端：0.10.9 起一律 401，改用 JWT Bearer。
- IIS 不再需要為 Blazor Server 開 WebSocket（通知推播與 AI 串流是一般的 HTTP 串流）。
