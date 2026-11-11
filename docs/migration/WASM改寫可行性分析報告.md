# WASM 改寫可行性分析報告

- 文件版本：1.4
- 文件狀態：規劃中
- 現行系統版本：0.10.9
- 首次實作版本：—（尚未實作，預計 0.10.0 起）
- 最後核對日期：2026/11/11

## 文件目的

評估把本系統從 **Blazor Server** 改寫為 **Blazor WebAssembly（WASM）＋獨立 Web API** 是否可行、要付出多少代價、怎麼做才能讓原有功能全部保留，並提出分階段的里程碑規劃。執行計畫與進度見 [WASM 遷移計畫](WASM遷移計畫.md)。

---

## 1. 結論摘要

| 問題 | 結論 |
|---|---|
| 可不可行？ | **可行**。沒有任何原有功能在 WASM 架構下做不到 |
| 工作量？ | **大型改寫**。主要成本是補 Web API：目前約 25 個功能區只能透過伺服器端服務使用，API 只涵蓋登入與 3 種資料的 CRUD |
| 怎麼做風險最低？ | **漸進式**：12 個里程碑，每個結束時整個系統都完整可用、可實機測試；最後兩個里程碑才拆成獨立前端站台並移除 Server 版 |
| 最難的三段 | ① 共用外框（選單、通知、說明）在新舊兩種模式下並存（M2）；② AI 助理的串流與「工具核准後才執行」流程（M8）；③ 登入改純 Token（M9） |
| 會不會變慢？ | 第一次開啟要下載程式本體（數 MB，視壓縮與修剪而定），之後由瀏覽器快取；操作時不再每次點擊都經過伺服器即時連線，反應通常較快 |

---

## 2. 改寫動機、目標與不做的事

### 2.1 目標（全部要兼顧）

1. **減輕伺服器負擔**：Blazor Server 每位使用者佔一條 SignalR 即時連線與一份伺服器記憶體；WASM 讓畫面在瀏覽器執行，伺服器只回應 API。
2. **前後端分離部署**：前端是一包靜態檔（可放靜態站台），後端只剩 Web API，可各自部署、各自擴充。
3. **弱網路不卡死**：程式本體以 PWA 快取在瀏覽器；斷線時畫面仍可開啟並顯示「目前離線」，恢復後繼續使用。
4. **腳手架示範**：本 Starter 改為 WASM 架構，供日後複刻的產品直接採用。
5. **為未來 App 鋪路**：登入改用 JWT Token，Web API 成為 WASM 前端與未來行動 App **共用的唯一後端**。

### 2.2 不做的事

- 不保留 Server 版（整個取代），也不做「兩種架構可選」。
- 不做離線編輯與資料同步；離線時只保證「不卡死、有提示」，資料讀寫仍需連線。
- 不開發行動 App，只讓 API 準備好。
- 不做「我的登入裝置清單」頁面（刷新 Token 紀錄表已預留欄位，未來需要時再加）。
- 不支援多台主機共用狀態（維持單台 IIS＋SQLite 的既有前提）。

---

## 3. 現況盤點

| 項目 | 數量／現況 |
|---|---|
| 可路由頁面（`@page`） | 37 個（7 個登入相關為靜態伺服器渲染，其餘為 `InteractiveServer`、不預先渲染） |
| UI 程式量 | 96 個 `.razor`（約 9,100 行）＋44 個 `.razor.cs`（約 15,300 行） |
| 頁面的資料存取方式 | 透過建構式／`[Inject]` 直接呼叫 `MyProject.Business` 服務，同一行程內執行 |
| 現有 Web API | 7 個 Controller：Auth（login/refresh/me）、Category、Team、Project 的 CRUD、專案附件下載、備份下載（後兩者只接受 Cookie）、Google 登入 |
| 資料存取路徑 | 兩套並存：畫面用 `Services/DataAccess/*Service`（`IDbContextFactory`，19 個），API 用 `Repositories/*Repository`（scoped DbContext，3 個） |
| 可直接給前端共用的專案 | `MyProject.Share`、`MyProject.Dtos`、`MyProject.Models` 只依賴 .NET 基礎類別庫，**可直接被 WASM 前端參考** |
| 「目前使用者」機制 | 電路範圍的 `CurrentUserService`，由 `AuthenticationStateHelper.Check()` 在每次換頁時填入（約 31 處呼叫），9 個以上的商業服務依賴它判斷「誰在操作」 |
| 伺服器推送 | `INotificationSignal` 行程內推播給鈴鐺、公告、首頁小工具（另有 60 秒輪詢備援）；排程與備份進度以計時器輪詢 |
| AI 功能 | 6 個畫面逐字串流；Agent 對話保存於伺服器並可接續，含約 63 個工具，寫入類工具要使用者核准後才執行；工作流程執行狀態只在記憶體 |
| 匯出／上傳 | Excel（ClosedXML）4 頁、CSV 4 頁、PDF（PDFsharp）5 處以上，皆伺服器產檔後經電路傳給瀏覽器；上傳只有專案附件 |
| 自動化測試 | 約 1,809 個（xUnit＋bUnit＋WebApplicationFactory），其中 37 個測試檔以資料夾路徑找 `MyProject.Web` |
| 腳本 | `New-CrudModule.ps1`（25 個文字錨點，產生 Page/View/Controller）、`New-StarterProject.ps1`（複刻）、`Invoke-QualityGate.ps1`（7 步品質閘門） |

---

## 4. 功能對照表（原功能 → WASM 後如何保留）

難度：低＝只換資料來源；中＝要補較多 API 或改互動方式；高＝要重新設計流程。

| 路由 | 功能 | 目前依賴的伺服器端能力 | WASM 後的做法 | 里程碑 | 難度 |
|---|---|---|---|---|---|
| `/Profile` | 個人資料 | ProfileService、CurrentUser 變更事件 | 個人資料 API；前端工作階段物件發出變更事件 | M2 | 低 |
| （外框） | 側邊選單、通知鈴鐺、公告橫幅、操作說明、共用元件 | Menu.json、說明檔讀取、Markdig、通知推播 | 選單與說明由 API 提供（權限過濾、Markdig 仍在伺服器）；通知改輪詢＋串流推播 | M2、M8 | 高 |
| `/categories` | 分類清單 | CategoryService、Excel 匯出 | API 改呼叫 Service；匯出改檔案端點 | M3 | 中 |
| `/teams` | 團隊清單 | TeamService、團隊樹快取、Excel 匯出 | 同上，另提供團隊樹 API | M3 | 中 |
| `/projects` | 專案項目 | ProjectService、附件上傳、ClosedXML | 附件上傳 API、一次性下載票證、匯出端點 | M4 | 中 |
| `/myusers` | 使用者管理 | MyUserService、雙因素重設、強制登出、Excel | 使用者 API（含複製、解鎖、強制登出），`[AdminOnly]` | M5 | 中 |
| `/roleviews` | 角色管理 | RoleViewService、權限矩陣 | 角色與權限目錄 API | M5 | 中 |
| `/audit-logs` | 稽核紀錄 | 查詢、CSV、雜湊鏈驗證 | 查詢／匯出／驗證 API | M5 | 低 |
| `/email-logs` | 郵件紀錄 | EmailOutboxQueryService | 查詢 API | M5 | 低 |
| `/logs` | 日誌檢視＋AI 分析 | 讀日誌檔、AI 串流、PDF | 查詢 API；AI 分析改串流端點；PDF 端點 | M6 | 高 |
| `/log-level-setting` | 日誌等級設定 | NLog 執行期調整 | 設定 API | M6 | 低 |
| `/system-exceptions` | 系統例外＋AI 分析與追問 | ExceptionLogService、AI 串流、PDF | 查詢 API；分析與追問對話存在伺服器快取（註：0.10.5 實作改為畫面送回問答、伺服器以資料庫重建例外明細與系統提示，不存伺服器快取，見 [WASM 遷移計畫](WASM遷移計畫.md) M6） | M6 | 高 |
| `/ai-call-logs` | AI 對話紀錄 | 檔案中的呼叫原文、PDF、合併 PDF | 查詢與 PDF 端點 | M6 | 中 |
| `/token-usage` | Token 用量 | 統計、趨勢圖、Excel/PDF | 統計 API、匯出端點 | M6 | 中 |
| `/prompt-templates` | AI 提示詞 | PromptTemplateService | 版本管理 API | M6 | 低 |
| `/system-health` | 系統健康監控 | 健康檢查、測試信、測試通知、AI 探測 | 健康 API 與各測試動作 API | M7 | 中 |
| `/database-usage` | 資料庫用量 | SQLite 檔案大小 | 查詢 API | M7 | 低 |
| `/scheduled-jobs` | 排程作業 | 排程器單例狀態、立即執行 | 狀態 API＋執行中每 1～2 秒輪詢 | M7 | 中 |
| `/backups` | 系統備份 | 備份檔、Cookie 下載 | 備份 API；下載改一次性票證 | M7 | 中 |
| `/data-retention` | 資料保留與清理 | 目錄大小、自訂清除 | 總覽與清除 API | M7 | 低 |
| `/system-parameters` | 系統參數 | 即時改寫設定來源 | 參數 API | M7 | 低 |
| `/announcements` | 公告管理 | 公告快取、廣播 | 公告 API；廣播走通知機制 | M7 | 低 |
| `/App` | 首頁儀表板 | 小工具目錄、通知、排程概況 | 各小工具資料 API，依權限顯示 | M7 | 中 |
| `/agent-playground` | Agent 測試台 | 記憶體中的 Agent 對話、工具核准、串流 | 每次請求以 `ResumeAsync` 接續已保存對話；串流 HTTP | M8 | 高 |
| `/agent-workflows` | Agent 工作流程 | 記憶體中的工作流程與人工介入 | 伺服器端執行登錄表（閒置逾時）；串流 HTTP | M8 | 高 |
| `/admin-assistant` | 系統管理助理 | 分派、約 63 個工具、核准、巡檢報告 | 同 Agent 測試台；每段對話一把鎖避免重複送出 | M8 | 高 |
| `/Auths/Login` | 登入（含驗證碼、記住我、鎖定） | 靜態頁 `HttpContext.SignInAsync` | 改 WASM 登入頁＋登入 API 發 Token | M9 | 高 |
| `/Auths/TwoFactor` | 雙因素驗證 | 加密的待驗 Cookie、記住裝置 Cookie | 待驗票證放在回應內容；記住裝置 Token 存 localStorage | M9 | 高 |
| `/Auths/ForgotPassword`、`/Auths/ResetPassword` | 忘記／重設密碼 | 靜態頁、寄信 | API＋WASM 頁；信件連結指向前端 | M9 | 中 |
| `/Auths/Pending` | Google 帳號待審 | 靜態頁 | WASM 頁 | M9 | 低 |
| `/Auths/Logout` | 登出 | `SignOutAsync` | 呼叫登出 API 撤銷刷新 Token、清除儲存 | M9 | 低 |
| `/Auths/RefreshSession` | 改密碼後重發 Cookie | 工作階段票證 | 不再需要（改密碼 API 直接回新 Token） | M9 移除 | 低 |
| `/ChangePassword` | 變更密碼 | 密碼原則、重發 Cookie | API 回新 Token 並撤銷其他工作階段 | M9 | 中 |
| `/TwoFactorSetup` | 雙因素設定 | QRCoder | QR 碼由伺服器產生 PNG 回傳（金鑰不在前端運算） | M9 | 中 |
| （Google 登入） | Google SSO | 外部 Cookie、MVC 轉址 | callback 產生一次性代碼導回前端，前端換 Token | M9 | 高 |
| `/` | 啟動頁 | — | WASM 頁 | M9 | 低 |
| `/Error`、`/not-found` | 錯誤頁、找不到頁 | `HttpContext` | WASM 頁 | M9 | 低 |

**伺服器電路專屬機制的替代方式：**

| 原機制 | 替代 |
|---|---|
| `ApplicationCircuitHandler` 每次換頁寫 `Page.View` 稽核 | 前端路由換頁時呼叫頁面瀏覽稽核 API |
| 電路取得用戶端 IP | 一般 HTTP 中介軟體取得 |
| `BrowserErrorReporter`（JS 呼叫 .NET） | 前端錯誤回報 API |
| 前端 `ILogger` 寫入伺服器日誌 | `RemoteLoggerProvider` 批次送 Warning 以上到伺服器，追蹤碼照常可查 |
| `ReconnectModal` 斷線重連畫面 | 離線提示列＋API 失敗訊息（M12） |
| 例外紀錄的「發生在哪個頁面」 | 前端每個請求帶 `X-Client-Page` 標頭 |
| 約 12 個 `IOptions<…Settings>` 直接注入畫面 | 匿名的 ui-config 與登入後的 ui-settings API（白名單欄位，測試保證不含密鑰） |

---

## 5. 技術決策與取捨

### 5.1 專案結構

| 專案 | 過渡期（M1～M10） | 最終（M11 起） |
|---|---|---|
| `MyProject.Web` | API＋Blazor Web App 主機，含尚未搬移的 Server 頁 | **只剩 Web API 主機**（名稱保留，避免牽動 NLog、Data Protection、複刻腳本與 37 個測試） |
| `MyProject.Web.Client`（新） | WASM 頁面、外框、共用元件、各功能區 API 用戶端 | 獨立靜態站台 |
| `MyProject.Share`／`Dtos`／`Models` | 前後端共用 | 同左 |
| `MyProject.Business`／`AccessDatas` | 只給後端用 | 同左 |

`Menu.json`、`HelpTopics.json`、`Datas/Help/*.md` 留在後端，由 API 提供（權限過濾與 Markdig 渲染維持在伺服器）；`theme.css`、`app.css`、`js/` 移到前端。

> **實作補記（0.10.9，M10）**：Server 頁在 M9 全部搬完，M10 移除 Server 互動模式（電路、`/_blazor`、重連畫面、`AuthenticationStateHelper`、Server 版外框）。M10 到 M11 之間 `MyProject.Web` 只剩 Web API、WASM 的主機頁 `Components/App.razor`（一律渲染 `ClientRoutes`）與靜態檔；外框 API 還在用的伺服器類別（選單 `SidebarMenuService`、操作說明 `PageHelpService` 與 Markdown 解析、公告快取 `AnnouncementCache` 等）暫留原本的 `Components/` 資料夾，M11 再搬。細節見 [WASM 遷移計畫](WASM遷移計畫.md) 的 M10 節。

### 5.2 認證：純 Token（為了未來 App）

| 項目 | 決定 | 理由 |
|---|---|---|
| 登入憑證 | JWT 存取 Token（建議 15 分鐘）＋不透明刷新 Token | 未來 App 共用；前端不需 Cookie |
| 存放位置 | 勾「記住我」→ localStorage；未勾 → sessionStorage | 與現在 Cookie 版的「記住我」行為最接近 |
| 刷新 Token | **新增伺服器端紀錄表**：每裝置一筆、每次使用即換新、舊 Token 被重用就撤銷整串、登出／改密碼／停用帳號／角色變更時撤銷 | 目前的刷新 Token 是無狀態 JWT，登出後無法失效；這是安全必要，也是 App 的基礎 |
| 多分頁 | 以瀏覽器 Web Locks 讓刷新排隊；sessionStorage 登入的新分頁向已開分頁要 Token | 避免兩個分頁同時刷新觸發重用偵測而被登出 |
| 強制改密碼／強制設定雙因素 | 伺服器端 API 一律擋（回 403 與錯誤代碼），不只靠畫面導頁 | 讓直接呼叫 API 也無法繞過 |
| 過渡期 | M1～M8 登入頁維持 Cookie；WASM 頁以 Cookie 向 `/auth/bridge` 換 Token 後只用 Token 呼叫 API；M9 一次改純 Token、停發 Cookie | 舊 Server 頁技術上只能用 Cookie；登入流程只重寫一次、風險最低 |

> **實作補記（0.10.8，M9）**：方向與上表一致（存取 Token 15 分鐘、localStorage／sessionStorage、Web Locks 排隊、API 擋強制設定），實作時補上與調整幾點，細節見 [WASM 遷移計畫](WASM遷移計畫.md) 的 M9 節：
> - **效期沿用 Cookie 版的語意**：勾「記住我」整串上限 30 天（從登入算、不滑動）；沒勾刷新 Token 閒置 480 分鐘（滑動，可設為絕對上限），而且存在 sessionStorage，關掉分頁就登出。設定區段 `SessionSettings` 取代 `CookieSettings`（舊鍵仍相容）。
> - **「新分頁向已開分頁要 Token」的做法**：已開分頁不是把自己的刷新 Token 交出去，而是在刷新鎖內呼叫 `session/fork` 為新分頁開一串新的——每個分頁各一串，輪替時不會互相觸發重用偵測。也因此「每裝置一筆」改成「每個分頁一串、以登入識別串起來」：登出與重用偵測都撤銷**同一次網頁登入的每一串**（只撤銷一串的話，偷到 Token 的人先 fork 一串就躲過偵測）。
> - **登出立即生效**：存取 Token 帶所屬那一串的識別，每個請求檢查是否已撤銷（同一行程立即、跨行程最多 30 秒），不必等存取 Token 到期。
> - **同一個瀏覽器只有一個身分**（本報告沒提到）：沒有 Cookie 之後，兩個分頁可以各登入不同的人；改成登入成功時廣播，顯示另一個人的分頁改用新的登入，與 Cookie 版行為相同。
> - **Google 的一次性代碼另綁定發起的瀏覽器**（第 4 節表格「Google 登入」列）：前端產生驗證值、只把雜湊帶給伺服器，兌換時出示原值，代碼在網址片段外流也沒用；代碼 60 秒、一次、只存雜湊。
> - **雙因素的待驗票證與記住裝置 Token**（第 4 節表格 `/Auths/TwoFactor` 列）：以 Data Protection 加密、綁使用者與工作階段版本；待驗票證放 sessionStorage（5 分鐘），記住裝置 Token 放 localStorage。
> - **Cookie 驗證在 M9 就移除**（第 7 節原本放 M10）：沒有流程發 Cookie，留著只會讓舊 Cookie 還有地方被讀；只留 Google 往返過程的暫存外部 Cookie。
> - **限流排在驗證之後、網頁登入不算嚴格登入配額**：原本排在驗證之前，全部依 IP 分區；網頁登入照 Cookie 版靠帳號鎖定。
> - M9 照第 7 節的建議拆成 9a（補 API、搬中性頁、登入方式不變）與 9b（真正切換），兩段一次合併。

### 5.3 資料存取：統一用 Services

淘汰 Category／Team／Project 的 Repositories，Controller 改呼叫既有 Services（稽核、團隊範圍、啟用切換、還原、併發戳記等邏輯都在 Services）。API 請求時由中介軟體依 JWT 填入 `CurrentUserService`，**商業服務本身不必修改**。

### 5.4 AI 串流：串流 HTTP（NDJSON），不用 SignalR

| 比較 | 串流 HTTP（採用） | SignalR Hub |
|---|---|---|
| Token 驗證 | 每個請求一般 Bearer 標頭 | 長連線只能把 Token 放網址參數，過期不會重驗、會進日誌 |
| `[HasPermission]`、限流 | 直接適用 | 不適用於 Hub |
| IIS | 需對串流關閉壓縮與緩衝 | 需啟用 WebSocket |
| 未來 App | 任何 HTTP 用戶端都能接 | 需 SignalR 用戶端 |

停止生成／關閉對話窗會中斷請求，伺服器端同步取消上游模型呼叫，既有的計費與取消行為不變。最終系統不含 SignalR。

### 5.5 通知

以輪詢為準（每 60 秒，切回分頁或自己操作後立即更新），另加一條 NDJSON 推播串流加速（M8），保留現在「鈴鐺即時更新」的體驗。

> **實作補記（0.10.7，M8）**：與本報告的方向一致，實作時補上幾點，細節見 [WASM 遷移計畫](WASM遷移計畫.md) 的 M8 節：
> - **通知推播**（`POST api/v1/notifications/stream`）：閒置每 25 秒送心跳（代理伺服器常把 60 秒以上沒資料的連線切斷）；Token 到期前 1 分鐘或滿 30 分鐘由伺服器結束、前端重連（長連線只在開始時檢查身分）；每位使用者最多 3 條（HTTP/1.1 每網域 6 條連線的保護），超過的分頁只靠輪詢；可用 `NotificationSettings:PushEnabled` 關閉。沒做跨分頁共用一條連線。
> - **Agent 對話的「每段對話一把鎖」**（第 3 節表格 `/admin-assistant` 列）：實作是**排隊**而不是擋下重複送出——兩個分頁同時操作同一段對話時後到的等前一個做完；「同一個核准只執行一次」另以資料庫的條件式更新保證（跨行程也成立）。Agent 對話與工作流程的串流同樣加心跳（15 秒）。
> - **工作流程執行登錄表**（第 3 節表格 `/agent-workflows` 列）：除了閒置逾時（30 分鐘），還讓「停在等人回覆時重新整理或關分頁」可以回到頁面自動接回；一人同時只能有一次執行。R7 所說的「IIS 回收遺失」照舊列為已知限制。

### 5.6 下載與上傳

- Excel／CSV／PDF：改為帶 Token 的檔案端點，前端取得後觸發下載，稽核照寫。
- 大檔（附件、備份）：先申請**一次性下載票證**（60 秒、綁使用者與安全戳記、使用時再驗權限），再由瀏覽器原生下載，不佔 WASM 記憶體。
- 上傳：`InputFile` 串流上傳到專案附件 API，伺服器與 IIS 雙重限制大小。

### 5.7 離線（PWA）

Service Worker 快取程式本體（不快取 `/api/*`）、離線提示列、離線時 API 顯示「目前離線，資料需要網路連線。」、新版本提示（快取版本綁 `SystemVersion`）。

---

## 6. 對工具鏈的影響

| 項目 | 影響 | 處理 |
|---|---|---|
| 37 個路徑耦合測試 | 頁面搬到 Client 後會找不到檔案 | M1 新增共用的 `SourceTree` 路徑工具，一次改完並加守門測試 |
| 慣例測試（色票、說明頁、按鈕圖示等） | 需同時掃描 Web 與 Client | `SourceTree.UiRoots` 涵蓋兩邊 |
| 頁面授權測試 | 目前反射 Web 組件 | 擴充為包含 Client 組件，並新增「每個 API action 都必須標註授權」的守門測試 |
| `New-CrudModule.ps1` | 樣板與錨點指向 Web | M3 改為產生 Client 頁面＋API 用戶端＋呼叫 Service 的 Controller，並以 `Test-CrudGenerator.ps1` 實跑 |
| `New-StarterProject.ps1` | 只讀一個 launchSettings | M11 改為處理前後端兩個主機的連接埠 |
| 品質閘門 | 方案層級執行，本身不綁路徑 | 不變；新專案加入 slnx 即被涵蓋 |
| 實機驗證 | 目前為手動 Playwright | M1 新增 `scripts/e2e/` 與 `Start-E2EHost.ps1`（隔離全部路徑、暫時 support 密碼） |

---

## 7. 里程碑規劃

| # | 版本 | 內容 | 規模 |
|---|---|---|---|
| M1 | 0.10.0 | 基礎建設：Client 專案、刷新 Token 紀錄、API 行為者、bridge、測試路徑統一、E2E 工具（不搬頁面） | 大 |
| M2 | 0.10.1 | 共用外框雙模式＋試點 `/Profile` | 特大 |
| M3 | 0.10.2 | 分類、團隊；淘汰 Repository；CRUD 產生器改新模式 | 大 |
| M4 | 0.10.3 | 專案、附件上傳、下載票證、匯出模式 | 大 |
| M5 | 0.10.4 | 使用者、角色權限、稽核紀錄、郵件紀錄；`[AdminOnly]` | 大 |
| M6 | 0.10.5 | AI 串流基礎＋日誌、日誌等級、例外、AI 對話紀錄、Token 用量、提示詞 | 特大 |
| M7 | 0.10.6 | 健康監控、資料庫用量、排程、備份、資料保留、系統參數、公告、首頁儀表板 | 大 |
| M8 | 0.10.7 | Agent 測試台、工作流程、系統管理助理＋通知推播串流 | 特大 |
| M9 | 0.10.8 | 純 Token 登入（登入、雙因素、Google、忘記／重設密碼、改密碼、雙因素設定）；停發 Cookie | 特大（風險最高，必要時拆 9a／9b） |
| M10 | 0.10.9 | 移除 Server 模式與 Cookie、電路相關程式 | 中 |
| M11 | 0.10.10 | 拆成獨立靜態站台＋API 主機；CORS、IIS 部署、複刻腳本 | 大 |
| M12 | 0.10.11 | PWA、離線提示、新版本提示、下載量優化、最終文件 | 中 |

每個里程碑的詳細範圍與實機情境見 [WASM 遷移計畫](WASM遷移計畫.md)。

---

## 8. 風險與對策

| # | 風險 | 對策 |
|---|---|---|
| R1 | 首次載入量大；過渡期跨新舊頁面是整頁重新載入 | Brotli 壓縮、修剪、延遲載入 AI 與管理組件；每份測試報告記錄載入量 |
| R2 | localStorage 中的 Token 可能被 XSS 竊取 | 嚴格 CSP、既有的安全 Markdown 渲染、短效存取 Token、刷新 Token 重用偵測 |
| R3 | 多分頁同時刷新 Token 互相干擾 | Web Locks 排隊＋專門測試 |
| R4 | 時區：WASM 的 `DateTime.Now` 是瀏覽器時間 | 前端不自行計算「今天」，日期預設由伺服器提供；DTO 日期序列化測試 |
| R5 | 只在前端檢查的權限只是外觀 | 搬任何管理頁之前先上 `[AdminOnly]` 與「每個 API 都要標註授權」守門測試 |
| R6 | WASM 的 API 呼叫量比 Server 版多 | M3 前調整每使用者限流分區 |
| R7 | IIS 緩衝或壓縮破壞串流；記憶體中的工作流程遇 IIS 回收遺失 | web.config 對串流關閉壓縮；工作流程遺失列為已知限制並在文件說明 |
| R8 | 約 2.4 萬行 UI 搬移造成回歸 | 依功能區分批搬移，每個里程碑都做 37 路由煙霧測試；M2 與 M9 加強測試 |

---

## 9. 驗證與測試報告方式

每個里程碑都會產出一份測試報告（`docs/migration/測試報告/M{nn}-<主題>.md`），格式比照 [系統管理助理 附錄 A](../features/系統管理助理.md)：

| 段落 | 內容 |
|---|---|
| A.1 測試策略 | 單元／元件測試、品質閘門、實機驗證、獨立程式審查各涵蓋什麼 |
| A.2 自動化測試 | 新增與修改的測試類別、驗證的行為、數量 |
| A.3 品質閘門 | 7 個步驟的結果與測試總數 |
| A.4 實機驗證 | 情境表（編號、步驟、預期、實際、判定）＋截圖；含 37 路由煙霧測試結果與首次載入量 |
| A.5 程式審查 | 獨立審查的發現與處理 |
| A.6 已知限制 | 尚未處理或刻意不處理的事項 |
| A.7 測試環境 | 主機、瀏覽器、隔離路徑、資料庫、AI 模型（AI 相關里程碑使用真實模型） |

最終（M12 後）依本報告第 4 節的對照表逐項打勾，做一次全功能回歸。
