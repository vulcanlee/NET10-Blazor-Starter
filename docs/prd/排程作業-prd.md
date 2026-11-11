# 排程作業 PRD

- 文件版本：1.17
- 文件狀態：已實作
- 現行系統版本：0.10.8
- 首次實作版本：0.9.96
- 最後核對日期：2026/11/11

## 一、目標與範圍

提供一致的**排程作業框架**：週期性的背景工作（清除過期資料、之後的備份、報表、彙總）以 cron 描述執行時間，
有執行紀錄、可在管理頁查看與手動觸發，並保證 IIS 回收或重疊回收時**同一時段只跑一次**。

- **範圍**：`IScheduledJob` 與註冊方式、排程器（`JobSchedulerWorker`）、執行器（`ScheduledJobRunner`）、狀態與執行紀錄（`ScheduledJobState`、`JobRun`）、
  管理頁 `/scheduled-jobs`、系統健康監控的「排程作業」項目、內建作業（清理、備份、密碼到期提醒，見 §四 表格），以及已軟刪除資料的自動永久刪除（0.9.97 起）。
- **非範圍**：在畫面上修改執行時間（cron 仍在 appsettings；保留天數 0.9.98 起可在「系統參數」頁修改，見 [系統參數 PRD](系統參數-prd.md)）；分散式佇列與重試策略。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/scheduled-jobs` | 系統管理 → 系統設定與維運 → 排程作業（`Menu.json` id=67；0.9.113 前在「統計與分析」）| 僅管理員（`CheckIsAdmin`；權限鍵 `角色_排程作業` 不進角色矩陣）| 系統管理員 |

## 三、畫面與欄位

- 表格：作業（名稱＋說明）、執行時間（cron）、下次執行（本地時間；停用顯示「已停用」、總開關關閉顯示「排程已關閉」）、
  上次執行（結果、開始時間、耗時、訊息）、啟用開關、操作（立即執行、執行紀錄）。
- 立即執行：確認後放入佇列，由排程器在背景執行；同一作業已在佇列或執行中時按鈕停用並提示。稽核 `Job.Trigger`。
- 啟用開關：寫入 `ScheduledJobState.IsEnabled` 並更新 `UpdatedAtUtc`（重新啟用不補跑停用期間的時段）。稽核 `Job.Enable`／`Job.Disable`。
- 執行紀錄窗：最近 50 筆的開始時間、結果、觸發方式（排程／補跑／手動，手動附帳號）、耗時、訊息、錯誤追蹤碼。
- 有作業在佇列或執行中時每 2 秒自動更新（0.10.6 起；之前 5 秒），結束或離開頁面就停；總開關關閉或設定被改壞時頁首顯示提示。

**WebAssembly（0.10.6 起，WASM 遷移 M7）**：頁面改在瀏覽器執行（`MyProject.Web.Client/Pages/ScheduledJobPage.razor`、`Views/Admins/ScheduledJobView.*`；Server 版已刪除），畫面與訊息和之前相同；開頁以 `Permissions.IsAdmin` 判斷。資料經只限管理員的 `ScheduledJobsController`（`api/v1/scheduled-jobs`：`GET` 總覽、`POST {name}/trigger`〔已在佇列或執行中 409〕、`PUT {name}/enabled`、`GET {name}/runs`〔最多 50 筆〕；作業名稱不分大小寫），稽核內容與 Server 版相同、由伺服器寫；時間以不帶時差送出。首頁「排程作業」小工具另有 `GET api/v1/dashboard/scheduled-jobs`（只限管理員）。

## 四、內部系統運作

- **作業**：實作 `IScheduledJob.ExecuteAsync(ScheduledJobContext, CancellationToken)` 回傳 `ScheduledJobResult`；以
  `AddScheduledJob<T>(名稱, 顯示名稱, 說明, 預設 cron)` 註冊 —— 描述（`ScheduledJobDescriptor`）為 singleton，作業類別為 scoped、每次執行在新的 scope 解析。
- **排程器** `JobSchedulerWorker`：啟動 → 建立缺少的狀態列、回收殘留的 Running → 暖機 30 秒 → 補跑；之後最多睡一分鐘，時段到了以**時段本身**為 ScheduledFor 執行。
  補跑錨點 = max(`LastScheduledForUtc`, `UpdatedAtUtc`)，錯過多次只補最近一次；全新安裝與新作業不補跑。
- **執行器** `ScheduledJobRunner`：
  1. 排程與補跑先以一條 UPDATE **搶占時段**（`LastScheduledForUtc < 時段` 且啟用才搶得到）—— 跨行程只跑一次、而且單調；手動不搶也不消耗時段。
  2. 試著取得作業的**檔案鎖**（`<資料庫檔>.job-<名稱>.lock`，`CrossProcessFileLock`）；拿不到就記一筆 Skipped。
  3. 拿到鎖代表沒有人在跑它，殘留的 Running 改成 Interrupted。
  4. 寫入 Running（先提交）→ 新的錯誤追蹤碼與 `ExceptionContext(背景作業, 作業名)` → 執行 → 寫回 Succeeded／Failed／Interrupted（不受取消影響）→ 依保留天數清除舊紀錄 → 釋放鎖。
- **立即執行**：頁面只放進 `ScheduledJobTriggerQueue`，由排程器以自己的執行環境啟動並在關機時等待。
- **時間**：cron 依伺服器本地時區（`TimeProvider.LocalTimeZone`）；資料庫的時間一律存 UTC，交給 Cronos 前以 `ScheduleCalculator.AsUtc` 標記。
- **內建作業**（預設每天 03:00）：

| 名稱 | 顯示名稱 | 天數設定 | 稽核 |
|---|---|---|---|
| `AuditLogRetention` | 稽核紀錄清理 | `LogRetentionSettings:AuditLogDays`（365，UTC 門檻，異動與安全事件）；0.9.174 起另有 `AuditBrowsingLogDays`（90，瀏覽類）分段清，各自 0 就略過；兩類都 >0 時才用較舊的門檻收內容檔月份目錄；兩類都 0 時結果「保留天數都設為 0，不自動清除。」| `Audit.AutoPurge`（每一類一筆，detail `rows=N; days=D; kind=change` 或 `kind=browsing`、`trigger=T`）|
| `AuditIntegrityVerify` | 稽核紀錄完整性檢查（0.9.175 起，預設 `30 3 * * *`，避開 02:00 的備份與 03:00 的清理）| —（不刪資料）：重算每一筆稽核紀錄的簽章並更新資料庫以外的檢查點；**發現問題時回 Failure**，由下方的失敗通知送全體管理員＋寄信（不另設通知分類）；問題另以 ERROR 寫進 NLog。一次性事件（金鑰變更、鏈頭重建、資料庫倒回）只報一次，持續性損壞每天報。見[稽核紀錄 PRD §九](稽核紀錄-prd.md) | `Audit.Verify`（每次都寫，detail `result=ok\|failed; signed=N; issues=M; legacy=L; keyMismatch=K; trigger=T`）|
| `ExceptionLogRetention` | 系統例外紀錄清理 | `LogRetentionSettings:ExceptionLogDays`（90，本地門檻）| `ExceptionLog.AutoPurge` |
| `AiCallLogRetention` | AI 對話紀錄清理 | `AiCallLogSettings:RetentionDays`（90）| `AiCallLog.AutoPurge` |
| `AiAgentConversationRetention` | Agent 對話清理（0.9.143 起；0.9.144 起含 Agent 工作流程的執行紀錄）| `AiAgentConversationSettings:RetentionDays`（90，依最後一次對話時間 `UpdatedAt`，全體使用者）| `AiAgentConversation.AutoPurge` |
| `AdminPatrolReportRetention` | 巡檢報告清理（0.9.161 起）| `AdminPatrolSettings:RetentionDays`（90，依產出時間 `CreatedAt`）| `AdminPatrol.AutoPurge` |
| `AdminPatrol` | 系統管理助理巡檢（0.9.161 起，預設 `30 7 * * *`）| `AdminPatrolSettings:RetentionDays`（報告保留天數）；AI 未設定時回成功並略過 | —（工具呼叫寫 `Ai.ToolInvoked`，操作者留空、註明「排程巡檢」；需要注意時發站內通知＋寄信） |
| `TokenUsageLogRetention` | Token 用量紀錄清理 | `LogRetentionSettings:TokenUsageLogDays`（365，分批刪除）| `TokenUsage.AutoPurge` |
| `SystemBackup` | 系統備份（0.9.99 起，預設 `0 2 * * *`）| `BackupSettings:KeepCount`（7 份，成功後才刪舊的）| `Backup.Create`／`Backup.AutoPurge` |
| `SoftDeletePurge` | 已刪除資料清理（0.9.97 起）| `SoftDeleteSettings:PurgeAfterDays`（90，本地門檻，比對 `DeletedAt`）| `Project`／`Category`／`Team`／`User`／`Role.AutoPurge`（每種一筆） |
| `NotificationRetention` | 站內通知清理（0.9.100 起）| `NotificationSettings:RetentionDays`（90，UTC 門檻，分批刪除）；0.9.125 起另依 `ReadRetentionDays`（7，從已讀時間起算）刪除已讀的 | `Notification.AutoPurge` |
| `EmailLogRetention` | 寄信紀錄清理（0.9.167 起）| `EmailDeliverySettings:RetentionDays`（90，UTC 門檻，分批刪除；「待寄」不刪）| `EmailLog.AutoPurge` |
| `RefreshSessionRetention` | 登入 Token 清理（0.10.0 起）| —（沒有保留天數可設，不在系統參數頁）：刪除已到期的刷新 Token 紀錄（`RefreshSession`），有效期依發出管道：App 由 `JwtSettings:RefreshTokenDays`（7）、網頁由 `SessionSettings`（0.10.8 起：記住我 30 天、沒勾閒置 480 分鐘）決定；已撤銷但未到期的保留，用來偵測舊 Token 被重複使用 | `RefreshSession.AutoPurge`（detail `rows=N; trigger=T`）|
| `PasswordExpiryReminder` | 密碼到期提醒（0.9.101 起，預設 `0 8 * * *`）| `PasswordPolicySettings:ExpiryDays`（0＝不過期，此時不動作）；7 天內到期者各通知一次 | —（只發站內通知） |

  清理類有刪到資料才寫稽核；天數 0 回成功但不動作（稽核紀錄完整性檢查每次都寫 `Audit.Verify`）。內建作業 0.10.0 起共 14 個（0.9.175 為 13 個）。刪檔一律經對應的 file store。

- **失敗通知**（0.9.100 起）：結果為 **Failed** 時（不含中斷、略過），`ScheduledJobRunner` 在寫回結果之後以新的 scope 經 `INotificationSender` 通知全體管理員＋手動觸發者（觸發者 Id 經 `ScheduledJobTriggerQueue` 與 `ScheduledJobContext.TriggeredByUserId` 傳遞），同時寄信、連結 `/scheduled-jobs`；通知失敗不影響作業結果。見 [站內通知與公告 PRD](站內通知與公告-prd.md)。

- **已刪除資料清理**（`SoftDeletePurgeService`）：
  - 系統層級，**不套團隊範圍**、不重用各服務的 `PurgeAsync`（背景作業沒有登入者，會被團隊檢查擋下）。
  - 順序：專案 → 分類 → 團隊 → 使用者 → 角色。每種先以 `AsNoTracking` 取候選，再**逐筆用新的 DbContext** 重新確認條件、追蹤中 `Remove`（DELETE 帶版本號：候選之後被還原 → 並行衝突 → 略過）。
  - 專案的附件實體檔在提交成功後經 `ProjectFileStore` 刪除；support 帳號與「預設角色」受保護不刪；仍被任何使用者當主要角色的角色留到下次（刪除當下被指派的外鍵錯誤也一樣）。
  - 每種資料各自 try/catch：第一個未預期的錯誤中止該種並記一次錯誤，其他種類照常；失敗前已刪的仍寫稽核，作業回 Failure。略過、使用中、受保護、附件檔刪不掉只列在訊息，不算失敗。
  - 取消時停在筆與筆之間、回傳部分結果；作業先寫稽核再把取消交回框架（記為「中斷」）。

## 五、權限與安全

- 管理員專屬；權限鍵刻意不進角色矩陣（`AdminOnlyPermissionTests`）。
- 執行紀錄的訊息不放原始例外，細節以錯誤追蹤碼到「系統例外紀錄」查；手動操作與自動清除都寫稽核。

## 六、錯誤與邊界

- 作業丟例外 → Failed，訊息附錯誤追蹤碼，系統例外紀錄來源為「背景作業」；作業回報失敗 → Failed，不重複記錯誤。
- 網站關閉時執行中的作業 → Interrupted；下次啟動、或拿到鎖時把殘留的 Running 改成 Interrupted。
- 鎖被佔用（同一作業的上一次還沒結束）→ Skipped。
- 設定驗證（啟動時）：作業名稱必須已註冊、cron 必須是 5 欄位且真的會觸發、保留天數 0～36500；執行中改壞則沿用上一份合法設定並記錯誤。
- ⚠️ IIS 閒置逾時會讓應用程式集區停止，排程跟著停 —— 部署要求見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)；逾期未執行會在系統健康監控顯示黃燈。
- 已知殘留：排程器依序執行到期的作業，一個長時間的作業會讓同一時段的其他作業晚一點開始。

## 七、驗收與測試

- `ScheduledJobFrameworkTests`：排程計算、設定驗證、跨行程只跑一次（暫存檔案資料庫＋兩個 DI 容器）、鎖、殘留回收、錯誤與取消、追蹤碼與 scope、排程器、健康監控判斷。
- `RetentionJobTests`：五個清理作業（稽核、例外、AI 對話紀錄、Agent 對話、Token 用量；時區、0 天、無資料、失敗回報、分批、取消、月份目錄）。
- `AdminPatrolJobTests`（0.9.161）：系統管理助理巡檢與巡檢報告清理；巡檢的窄門、判讀與通知另見 `ScheduledPatrolToolGuardTests`、`AdminPatrolReportParserTests`、`AdminPatrolNotificationTests`。
- `SoftDeletePurgeTests`（0.9.97）：五種資料的門檻邊界、附件實體檔與根目錄檢查、提交後才刪檔、中途還原與並行衝突、角色使用中與刪除當下被指派、同輪使用者與角色、受保護、Cascade 範圍、單一種類失敗、取消、本地時鐘、稽核內容與截斷。
- `AuditIntegrityVerifierTests`（0.9.175）：完整性檢查抓得到各種竄改、一次性事件只由排程報一次、正常清除不誤報。
- `ScheduledJobWiringTests`：真正的主機中作業已註冊（0.10.0 起 14 個）、舊計時器已移除、排程器註冊順序。
- `OpsDashboardApiTests`（0.10.6 起）：總覽時間不帶時差、立即執行後顯示執行中且再按一次 409、啟用／停用稽核、不認得的作業 404、非管理員 403；實機 `scripts/e2e/m07-ops.mjs` M7-P1（立即執行後畫面自動更新）。
- 守門：`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`PageHelpCatalogTests`、`LoggingConventionTests`、`DataAccessServiceLifetimeTests`、`OptionsValidationTests`、`DocumentationConventionTests`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Scheduling/`（框架與 `Jobs/`）、`Configuration/ScheduledJobSettings.cs`、`Configuration/Validation/ScheduledJobSettingsValidator.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/ScheduledJobRunService.cs`、`Helpers/CrossProcessFileLock.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/SoftDeletePurgeService.cs`、`Services/Other/ProjectFileStore.cs`、`MyProject.Models/Systems/SoftDeleteSettings.cs`
- `src/MyProject/MyProject.AccessDatas/Models/JobRun.cs`、`ScheduledJobState.cs`
- `src/MyProject/MyProject.Web.Client/Views/Admins/ScheduledJobView.razor(.cs)`（0.10.6 起 WASM）、`Api/OpsApi.cs`（`ScheduledJobApi`）、`MyProject.Web/Controllers/ScheduledJobsController.cs`、`Datas/Help/scheduled-jobs.md`
- 設定：[日誌與設定檔說明 §4.11](../operations/日誌與設定檔說明.md)；規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)「排程作業」
