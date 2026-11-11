# prd — 產品需求文件主控台

- 文件版本：2.41
- 文件狀態：維護中
- 現行系統版本：0.10.4
- 首次實作版本：0.4.23
- 最後核對日期：2026/11/11

本目錄是產品需求的單一入口。PRD 以**產品能力**為單位；「已實作／部分實作」描述程式現況，「規劃中」必須獨立分區，不代表系統已提供。本專案為通用 Blazor 腳手架，**內建 LLM 日誌分析、AI 例外分析（0.9.68 起，系統例外紀錄明細窗，含針對單筆例外的多輪追問）、Token 用量／費用計價、AI 對話紀錄（0.9.72 起，管理員專屬、保存完整 Prompt／Response）、AI Agent（0.9.142 起，會呼叫工具的 Agent 與多 Agent 工作流程，見「AI Agent」PRD）與系統管理助理（0.9.155 起，管理員專屬的 AI 代理人，異動須經核准；0.9.161 起每天定時巡檢，見「系統管理助理」PRD）**（見「日誌檢視」「系統例外紀錄」「Token 用量」「LLM 呼叫費用估算」「AI 對話紀錄」PRD，機制見 [AI 日誌分析](../features/AI日誌分析.md)、[AI 例外分析](../features/AI例外分析.md)），但**不含 RAG／向量檢索／通用對話機器人**；PRD 內容一律以程式碼、`Menu.json` 與測試為準。

## 一、能力覆蓋矩陣

| 產品能力 | PRD | 入口／路由 | 主要程式來源 | 狀態 | 核對版本 |
|----------|-----|-----------|--------------|------|----------|
| 首頁與導覽 | [首頁與導覽](首頁與導覽-prd.md) | `/`、`/App` | `Pages/Home.razor`、`Pages/HomeAuthed.razor`、`SidebarMenuService`、`Menu.json`、`MainLayout`（含「關於」對話窗）、`PageHelpDialog`／`PageHelpService`／`Datas/HelpTopics.json`＋`Datas/Help/*.md`（頂欄「操作說明」對話窗，每個登入後頁面一份） | 已實作（0.9.66 起含頁面操作說明對話窗；0.9.121 起說明窗可切換字體小／中／大）| 0.9.163 |
| 登入與帳號流程 | [登入與帳號流程](登入與帳號流程-prd.md) | `/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`、`/ChangePassword` | `Components/Auths/*`、`MyUserServiceLogin`、`ExternalLoginService`、`PasswordResetService`、`AuthController` | 已實作（0.9.60 起含忘記密碼）| 0.9.163 |
| 專案項目 | [專案項目](專案項目-prd.md) | `/projects` | `Web.Client/Pages/ProjectPage.razor`（0.10.3 起 WASM）、`ProjectService`、`ProjectController`、`DownloadsController` | 已實作（0.9.78 起增刪改與附件上傳／刪除寫稽核；0.9.145 起 Agent 測試台可產生／清除測試專案；0.10.3 起改用 WebAssembly、API 走服務、標題不可重複、附件以一次性下載票證下載）| 0.10.3 |
| 使用者管理 | [使用者管理](使用者管理-prd.md) | `/myusers` | `Web.Client/Pages/MyUserPage.razor`（0.10.4 起 WASM）、`MyUserService`、`UsersController` | 已實作（0.9.120 起點狀態標籤切換啟用／停用；0.10.4 起改用 WebAssembly，新增管理員專用 API `api/v1/users`（`[AdminOnly]`），不可強制登出自己、不可重設自己的兩步驟驗證也在 API 擋，改自己後經 `SessionRefreshUrl` 換發登入，WASM 遷移 M5）| 0.10.4 |
| 角色管理 | [角色管理](角色管理-prd.md) | `/roleviews` | `Web.Client/Pages/RoleViewPage.razor`（0.10.4 起 WASM）、`RoleViewService`、`RbacWriteService`、`RolesController` | 已實作（0.10.4 起改用 WebAssembly，新增管理員專用 API `api/v1/roles`（`[AdminOnly]`，權限矩陣為樹狀 DTO），WASM 遷移 M5）| 0.10.4 |
| 分類清單 | [分類清單](分類清單-prd.md) | `/categories` | `MyProject.Web.Client` 的 `Pages/CategoryPage.razor`、`Views/Categories/CategoryViewView`、`Api/CategoryApi`；`CategoryController`（`/api/v1/Category`）、`CategoryService` | 已實作（0.4.40 起可指定適用團隊；0.9.78 起增刪改寫稽核；0.9.120 起點狀態標籤切換啟用／停用；0.10.2 起在 WebAssembly 執行，API 改走 `CategoryService`，WASM 遷移 M3） | 0.10.2 |
| 團隊清單 | [團隊清單](團隊清單-prd.md) | `/teams` | `MyProject.Web.Client` 的 `Pages/TeamPage.razor`、`Views/Teams/TeamViewView`、`Api/TeamApi`；`TeamController`（`/api/v1/Team`）、`TeamService` | 已實作（0.9.78 起增刪改寫稽核；0.9.120 起點狀態標籤切換啟用／停用；0.10.2 起在 WebAssembly 執行，API 改走 `TeamService`，WASM 遷移 M3）| 0.10.2 |
| 系統健康監控 | [系統健康監控](系統健康監控-prd.md) | `/system-health` | `Pages/SystemHealthPage.razor`、`Health/SystemHealthService` 等 | 已實作（14 項檢查；0.9.59 起含寄信服務項與寄信測試；0.9.79 起含日誌管線項；0.9.96 起含排程作業項；0.9.122 起含站內通知測試；0.9.175 起檔案系統項含稽核金鑰目錄）| 0.9.175 |
| 日誌檢視 | [日誌檢視](日誌檢視-prd.md) | `/logs` | `Pages/Analytics/LogViewerPage.razor`、`LogQueryService`、`NLogFilePathResolver`、`AiLogAnalysisService` | 已實作（0.9.69 起可展開收合與複製；0.9.78 起可依錯誤追蹤碼篩選；0.9.111 起有快速區間與「查前後 1 分鐘」；0.9.141 起 AI 分析串流顯示、可停止產生）| 0.9.163 |
| 資料庫用量 | [資料庫用量](資料庫用量-prd.md) | `/database-usage` | `Pages/Analytics/DatabaseUsagePage.razor`、`DatabaseUsageService` | 已實作 | 0.9.163 |
| 日誌等級設定 | [日誌等級設定](日誌等級設定-prd.md) | `/log-level-setting` | `Pages/Analytics/LogLevelSettingPage.razor`、`LogLevelRuntimeState` | 已實作 | 0.9.163 |
| 系統例外紀錄 | [系統例外紀錄](系統例外紀錄-prd.md) | `/system-exceptions` | `Pages/Admins/ExceptionLogPage.razor`、`Diagnostics/ExceptionLogProvider`、`ExceptionLogService`、`AiExceptionAnalysisService`、`CrashMarkerStore`、`ProcessExceptionHooks` | 已實作（0.9.77 補齊漏記、重複與帳號；0.9.78 加上錯誤追蹤碼、告警、自動清理）| 0.9.163 |
| Token 用量 | [Token 用量](Token用量-prd.md) | `/token-usage` | `Pages/Analytics/TokenUsagePage.razor`、`ITokenUsageRecorder`、`TokenUsageLogService`、`TokenUsageRawStore` | 已實作（0.9.17 起每列含費用估算；0.9.50 起有「最近 1／7／30 天」摘要卡與每日費用趨勢；0.9.51 起 PDF 可只匯出目前頁籤且含趨勢；0.9.55 起有折線趨勢圖頁籤，PDF 同步輸出；0.9.141 起串流途中停止的呼叫記一筆「已取消」；0.9.142 起加 `RunId`、作業「Agent 測試台」；0.9.144 起作業「專案狀態報告」「多觀點審查」；0.9.155 起作業「系統管理助理」「系統管理助理（分派）」；0.9.161 起作業「系統管理助理（排程巡檢）」）| 0.9.163 |
| AI 對話紀錄 | [AI 對話紀錄](AI對話紀錄-prd.md) | `/ai-call-logs`（`?callId=` 深連結）| `Pages/Analytics/AiCallLogPage.razor`、`IAiCallLogRecorder`、`AiCallLogService`、`AiCallLogFileStore`、`AiCallCapture`、`AiCallLogRetentionJob` | 已實作（0.9.72；0.9.96 起自動過期改由排程作業執行；0.9.141 起串流呼叫保存 SSE 原文；0.9.142 起明細顯示執行編號與同一次執行；0.9.144 起含 Agent 工作流程；0.9.149 起解析 Responses API 格式、顯示工具呼叫與工具結果；0.9.155 起含系統管理助理與分派，0.9.161 起含定時巡檢）| 0.9.163 |
| 系統管理助理 | [系統管理助理](系統管理助理-prd.md) | `/admin-assistant`（管理員專屬）| `Ai/Agents/AdminAssistant/*`（`AdminAssistantTurnPlanner`、`AdminAssistantAgent`、`MonitoringAssistantTools`、`OpsAssistantTools`、`AccountsAssistantTools`、`AiAssistantTools`、`AdminAssistantMutation`、`AdminPatrolRunner`、`ScheduledPatrolToolGuard`、`AdminPatrolReportParser`）、`Scheduling/Jobs/AdminPatrolJob`、`Ai/Agents/AiAgentTurn.cs`、`Views/Admins/AdminAssistantView` | 已實作（0.9.155 里程碑 1：自動分派領域、監控與診斷及排程的唯讀工具；0.9.157 里程碑 2：四大類共 26 個唯讀工具與巡檢報告；0.9.158 里程碑 3：22 個經核准才執行的可復原異動工具；0.9.159 里程碑 4：15 個不可復原的異動工具與紅字＋輸入確認的核准窗；0.9.161 第二版定時巡檢：排程作業 `AdminPatrol` 預設每天 07:30、存「巡檢報告」、需要注意時站內＋Email 通知全體管理員）| 0.9.163 |
| AI Agent | [AI Agent](AI-Agent-prd.md) | `/agent-playground`（Agent 測試台）、`/agent-workflows`（Agent 工作流程）| `Ai/Agents/*`（`AiAgentRunner`、`AiAgentToolFactory`、`AiToolApprovals`）、`Ai/Workflows/*`（`AiWorkflowRunner`、`AiAgentStep`）、`AiAgentFactory`、`AiAgentConversationService` | 已實作（0.9.142 測試台與工具核准；0.9.143 對話保存；0.9.144 工作流程；0.9.145 測試專案；0.9.149 起走 Responses API、推論等級 `AiSettings:AgentReasoningEffort`）| 0.9.163 |
| 系統備份與還原 | [系統備份與還原](系統備份與還原-prd.md) | `/backups`、`GET /api/backups/{檔名}/download` | `Pages/Admins/BackupPage.razor`、`Web/Backup/*`、`SystemBackupJob`、`BackupController`、`scripts/Restore-Backup.ps1` | 已實作（0.9.99；0.9.143 起可選備份 Agent 對話，0.9.144 起含工作流程的執行紀錄，0.9.161 起含巡檢報告的內容檔；0.9.175 起刻意不含稽核防竄改金鑰）| 0.9.175 |
| 資料保留與清理 | [資料保留與清理](資料保留與清理-prd.md) | `/data-retention` | `Pages/Admins/DataRetentionPage.razor`、`Web/DataRetention/*`、`Scheduling/RetentionCutoffs`、各資料服務的 `GetRetentionStatsAsync` | 已實作（0.9.133；0.9.134 起含自訂清除；0.9.143 起含 Agent 對話，0.9.144 起含工作流程的執行紀錄；0.9.161 起含巡檢報告；0.9.174 起稽核分兩類保留天數）| 0.9.174 |
| 個人資料 | [個人資料](個人資料-prd.md) | `/Profile`（右上角使用者選單）| `MyProject.Web.Client` 的 `Pages/ProfilePage.razor`、`Views/Profiles/ProfileView`、`Layout/UserInitials`；`ProfileController`（`GET`／`PUT /api/v1/profile`）、`ProfileService` | 已實作（0.9.102；0.9.123 起登入紀錄顯示來源 IP；0.10.1 起在 WebAssembly 執行，WASM 遷移 M2 試點）| 0.10.1 |
| AI 用量配額 | [AI 用量配額](AI用量配額-prd.md) | 全系統與每人的每日、每月新台幣上限，送出前擋下、80%／100% 通知 | `AiQuotaService`、`AiAccountingChatClient` | 已實作（0.9.109；0.9.140 起攔截移至記帳中介層，Agent 與系統管理助理的每一次模型呼叫都檢查；沒有登入使用者的定時巡檢只受全系統上限）| 0.9.163 |
| AI 提示詞管理 | [AI 提示詞管理](AI提示詞管理-prd.md) | 管理員修改 AI 日誌分析、例外分析、Agent 測試台（0.9.142 起）、Agent 工作流程各 agent（0.9.144 起）與系統管理助理（0.9.155 起，6 個）的提示詞，版本保留可切回；固定規則自動附加 | `PromptTemplateService`、`Web/Ai/AiPromptGuardrails.cs` | 已實作（0.9.108）| 0.9.163 |
| 通用匯出 | [通用匯出](通用匯出-prd.md) | 專案、分類、團隊、使用者頁「匯出 Excel」；診斷頁 CSV | `Web/Export/*` | 已實作（0.9.107）| 0.9.163 |
| 首頁儀表板 | [首頁儀表板](首頁儀表板-prd.md) | `/App` 的小工具區 | `Web/Dashboard/*`、`Views/Dashboard/*`、`HomeWelcomeView` | 已實作（0.9.106；0.9.122 起「我的通知」每 60 秒更新；0.9.123 起「上一次登入」顯示 IP）| 0.9.163 |
| 兩步驟驗證 | [兩步驟驗證](兩步驟驗證-prd.md) | `/TwoFactorSetup`（個人資料頁）、`/Auths/TwoFactor` | `TwoFactorService`、`TwoFactorLoginCookies`、`Views/Profiles/TwoFactorSetupView`、`Auths/TwoFactor` | 已實作（0.9.104）| 0.9.163 |
| 站內通知與公告 | [站內通知與公告](站內通知與公告-prd.md) | 頂欄鈴鐺、頁面上方公告橫幅、`/announcements` | `INotificationSender`、`NotificationSignal`、`Layout/NotificationBell`、`AnnouncementBanner`、`Pages/Admins/AnnouncementPage.razor`、`NotificationRetentionJob` | 已實作（0.9.100；0.9.125 起鈴鐺可刪除單則與清除已讀，已讀通知另依保留天數清除；0.9.161 起定時巡檢需要注意時通知管理員）| 0.9.163 |
| 系統參數 | [系統參數](系統參數-prd.md) | `/system-parameters` | `Pages/Admins/SystemParameterPage.razor`、`Web/Configuration/Parameters/*`、`SystemParameterService`、`SystemIdentity` | 已實作（0.9.98；0.9.142 起含「AI 模型」；0.9.143 起含 Agent 對話保留與備份；0.9.144 起含工作流程的模型；0.9.155 起含系統管理助理的模型；0.9.161 起含巡檢報告保留天數；0.9.174 起含稽核記錄範圍的兩個開關與瀏覽類保留天數）| 0.9.174 |
| 排程作業 | [排程作業](排程作業-prd.md) | `/scheduled-jobs` | `Pages/Admins/ScheduledJobPage.razor`、`Web/Scheduling/*`、`ScheduledJobRunService`、`CrossProcessFileLock`、`SoftDeletePurgeService` | 已實作（0.9.96；0.9.97 起含已刪除資料清理；0.9.143 起含 Agent 對話清理，0.9.144 起含工作流程的執行紀錄；0.9.161 起含系統管理助理巡檢與巡檢報告清理；0.9.174 起稽核紀錄清理分兩類；0.9.175 起含稽核紀錄完整性檢查）| 0.9.175 |
| 紀錄分類與團隊權控 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md) | 跨功能（所有清單查詢／檔案）| `PermissionChecker`、`EffectiveTeamResolver`、`RecordAccessScopeProvider`、`TagStringHelper` | 已實作 | 0.9.163 |
| 稽核紀錄 | [稽核紀錄](稽核紀錄-prd.md) | `/audit-logs` | `Web.Client/Pages/AuditLogPage.razor`（0.10.4 起 WASM）、`AuditLogsController`、`AuditLogQueryService`、`AuditLogService`（寫入）| 已實作（0.9.42 補上查詢畫面；0.9.117 起顯示來源 IP，0.9.123 起可依來源 IP 篩選；0.9.173 起可依目標類型＋目標識別精確查詢，五個資料頁每列有管理員專用的「異動歷程」；0.9.174 起修改前後差異與瀏覽類事件改為可設定、預設不記，瀏覽類另有保留天數；0.9.175 起防竄改簽章（雜湊鏈）與完整性驗證；0.10.2 起異動歷程另有管理員專用 API `/api/v1/audit-logs`，供 WASM 頁面使用；0.10.4 起本頁改用 WebAssembly，查詢、明細、匯出、清除、清空與驗證都有 `[AdminOnly]` API，「發生時間」排序修正為依點選方向，WASM 遷移 M5）| 0.10.4 |

## 二、無選單入口的核心能力

| 能力 | PRD 歸屬 | 現況 |
|------|----------|------|
| 動作級授權（`[HasPermission("resource:action")]`）與管理員短路 | [紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[角色管理](角色管理-prd.md) | 已實作，UI 與 API 共用單一 RBAC 權威 |
| 稽核事件的**寫入點**（登入、使用者/角色/權限異動；0.9.78 起擴及分類、團隊、專案與附件、登出、SSO、頁面權限拒絕、匯出與清除，代碼集中在 `AuditActions`；0.9.117 起每筆由 `AuditLogService` 自動記來源 IP）| [稽核紀錄](稽核紀錄-prd.md)、[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[分類清單](分類清單-prd.md)、[團隊清單](團隊清單-prd.md)、[專案項目](專案項目-prd.md) | 已實作；0.9.42 起查詢畫面見 `/audit-logs`，本列只涵蓋散落各流程的寫入點 |
| 帳號安全（PBKDF2、帳號鎖定、忘記密碼、兩步驟驗證）| [登入與帳號流程](登入與帳號流程-prd.md)、[兩步驟驗證](兩步驟驗證-prd.md) | 已實作；忘記密碼需啟用寄信（0.9.60）；兩步驟驗證 0.9.104 起由使用者自選開啟，可依角色或系統參數強制 |
| 檔案上傳（專案附件）| [專案項目](專案項目-prd.md) | 已實作 |
| 寄信服務（None／Pickup／Smtp、背景佇列、健康監控項與測試寄信）| [寄信服務](寄信服務-prd.md) | 已實作（0.9.59）；設定在 `EmailSettings`（Gmail 設定步驟見 [Gmail 寄信設定指南](../operations/Gmail寄信設定指南.md)），畫面在「系統健康監控」；管理員的「寄信紀錄」頁 `/email-logs`（0.9.167 起；0.10.4 起以 WebAssembly 執行，`Web.Client/Pages/EmailLogPage.razor`，資料走只限管理員的 `POST api/v1/email-logs/search`）|
| 日誌與例外處理（全系統共用規範：等級規則、例外分類、未捕捉例外涵蓋、稽核範圍、保存期限、告警）| [日誌與例外處理](日誌與例外處理-prd.md) | 已實作（0.9.76 立案；0.9.77 完成 P0、0.9.78 完成 P1、0.9.79 完成 P2）；§三、§四 為所有功能的開發與驗收依據 |
| LLM 呼叫費用估算（單價／匯率設定、四種計費單位、單價快照）| [LLM 呼叫費用估算](LLM呼叫費用估算-prd.md) | 已實作（0.9.17）；表現在「Token 用量」頁，維護入口是 `appsettings.json` |

## 三、規劃中產品藍圖

| 藍圖 | 現況界線 |
|------|----------|
| 軟體專案交接知識助理 | **暫停中**（2026/11/07 使用者決定重新評估題目，待決事項見文件開頭），見 [方向、價值說明與開發計畫](軟體專案交接知識助理-方向價值與開發計畫.md)。這是產品方向文件、不是能力 PRD，不適用第四節規則 4 |
| 各能力後續構想 | 見各 PRD 的「規劃中需求」章節，不屬於目前的驗收範圍 |

## 四、PRD 維護規則

1. 新增選單頁時，PRD、此覆蓋矩陣、`Menu.json` 與 `SidebarMenuService.MenuPermissionMap` 權限鍵必須同步。
2. 新增無頁面核心能力時，仍須指定一份產品能力 PRD，不得只留實作文件。
3. 現行需求以程式碼、設定與測試為準；文件衝突時校正 PRD 並留下 changelog。
4. 每份 PRD 必須標示文件版本、狀態、現行系統版本、首次實作版本及最後核對日期。
5. 未實作內容只能放在獨立「規劃中需求」章節；部分實作須逐項列出完成／未完成。
6. 文件使用 UTF-8 繁體中文含 BOM；提交前執行 `scripts/Test-DocsEncoding.ps1`。

> 返回 [文件總索引](../README.md)
