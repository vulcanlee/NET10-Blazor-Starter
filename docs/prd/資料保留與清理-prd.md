# 資料保留與清理 PRD

- 文件版本：1.11
- 文件狀態：已實作
- 現行系統版本：0.10.22
- 首次實作版本：0.9.133
- 最後核對日期：2026/11/14

## 一、目標與範圍

讓管理員在**一頁**看到系統裡每一種會自動刪除的資料：現在多少、佔多少空間、最舊到何時、保留規則、下一次自動清理會刪掉多少，以及負責的排程；需要時立即清理，或跳到可以細部刪除的頁面。

- **範圍**：管理頁 `/data-retention`（唯讀總覽＋「立即清理」＋「前往」＋「修改」連結）；各清理服務新增與清理共用條件的統計方法；`?deleted=1`（五個軟刪除頁直接顯示已刪除）與系統參數頁 `?group=`（捲到分類）。
- **非範圍**：在這一頁修改保留天數（只在系統參數頁）；在這一頁挑選個別紀錄刪除（在各頁）；修改清理排程時間（設定檔）；日誌檔與公告的清理（日誌由 NLog 處理，公告不自動刪除，只顯示資訊）；Web API。

0.9.134 起加上「自訂清除」：使用者自己選「只留最近 N 天」或「刪除某一天之前」，預覽後確認刪除，不改系統參數。
0.9.143 起多一列「Agent 對話」（排在 AI 對話紀錄之後；0.10.22 起只有系統管理助理的對話）。0.9.161 起在「Agent 對話」之後多一列「巡檢報告」（系統管理助理的定時巡檢）。

使用者決定（2026/10/21）：新增一頁、不擴充排程作業頁；每種資料一列，全部列出（已刪除資料分 5 列，共用一個排程）；顯示筆數、檔案空間、最舊一筆、下次會清掉幾筆；「立即清理」＋「前往」，系統備份只有「前往」；保留天數只顯示，連到系統參數修改。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/data-retention` | 系統管理 → 系統設定與維運 → 資料保留與清理（`Menu.json` id=68）| 僅管理員（WASM 頁 `Permissions.IsAdmin`＋API `[AdminOnly]`；權限鍵 `角色_資料保留與清理` 不進角色矩陣）| 系統管理員 |

## 三、畫面與欄位

- 工具列：重新整理。排程總開關關閉、排程設定有誤時顯示警示。
- 表格（固定 18 列，依序）：已刪除的專案／分類／團隊／使用者／角色、系統例外紀錄、Token 用量、AI 對話紀錄、Agent 對話（0.9.143 起）、巡檢報告（0.9.161 起）、稽核紀錄、站內通知、寄信紀錄（0.9.167 起）、登入 Token（0.10.0 起）、系統備份、排程執行紀錄、日誌檔、公告。
- 欄位：資料（附一併處理的檔案）、目前數量、檔案空間、最舊一筆、保留規則（＋「修改」→ `/system-parameters?group=資料保留|備份`）、下次會清掉、自動清理（白話頻率、停用標籤、上次時間與結果訊息、下次執行；已刪除的後四列只註明與專案同一個排程）、操作（「立即清理」「自訂清除」「前往」，0.9.134 起固定在右側）。
- 自訂清除窗（0.9.134 起，適用例外、Token、AI 對話、Agent 對話（0.9.143 起）、稽核、站內通知、寄信紀錄（0.9.167 起，「待寄」不刪）、排程執行紀錄與已刪除的五種；巡檢報告、登入 Token、備份、日誌檔、公告沒有）：二選一「只留最近 N 天」（1～3650）／「刪除這一天之前（不含當天，不可晚於今天）」，即時預覽會刪幾筆；「清除」→ 紅色確認 → 在頁面上同步刪除 → 通知刪了幾筆（已刪除資料附留下幾筆）。
- 一列讀取失敗只在那一列顯示「讀取失敗：例外類型」，其他列照常。
- **WebAssembly（0.10.6 起，WASM 遷移 M7）**：頁面改在瀏覽器執行（`MyProject.Web.Client/Pages/DataRetentionPage.razor`、`Views/Admins/DataRetentionView.*`；Server 版已刪除），畫面與訊息和之前相同；開頁以 `Permissions.IsAdmin` 判斷。資料經只限管理員的 `DataRetentionController`（`api/v1/data-retention`：`GET` 總覽〔含伺服器的「今天」〕、`POST {種類}/purge-now`、`POST {種類}/preview`、`POST {種類}/custom-purge`〔長時間請求，WASM 用 `PostLongRunningAsync`〕），稽核與 Server 版相同、由伺服器寫；清理進行中仍每 5 秒自動更新。
  與 Server 版不同：立即清理稽核 `Job.Trigger` 的 `expected=N` 由伺服器在按下時重新讀總覽算出（可能和確認窗的數字差幾筆；那一列現在不能清理時回 409 並說明原因）；自訂清除的條件由伺服器以自己的「今天」驗證。
- 「稽核紀錄」列（0.9.174 起分兩類）：保留規則「異動與安全事件保留 N 天；瀏覽類（換頁、搜尋、查看）保留 M 天」（`LogRetentionSettings:AuditLogDays`／`AuditBrowsingLogDays`，某一類為 0 時那一段為「不自動刪除（保留天數為 0）」）；目前數量、最舊一筆與下次會清掉是兩類合計（`AuditLogQueryService.GetRetentionStatsAsync(threshold, AuditLogKind.Change／Browsing)` 各算一次）；兩類都是 0 時才停用「立即清理」；自訂清除不分類（`AuditLogKind.All`）。
- 「Agent 對話」列（列鍵 `agent-conversation`）：附註「全體使用者，連同內容檔；依最後一次對話時間」、數量單位「段」、保留規則「最後一次對話後保留 N 天」、「前往」`/admin-assistant`（只看得到自己的對話；0.10.22 起，之前前往已移除的 Agent 測試台）。
- 「巡檢報告」列（0.9.161 起，列鍵 `admin-patrol-report`）：附註「系統管理助理的定時巡檢，連同內容檔；內容檔在 Agent 對話目錄底下，資料夾大小也算進「Agent 對話」」、數量單位「份」、保留規則「產出後保留 N 天」（`AdminPatrolSettings:RetentionDays`）、負責作業「巡檢報告清理」、「前往」`/admin-assistant`；沒有自訂清除。
- 「登入 Token」列（0.10.0 起，列鍵 `refresh-session`，排在寄信紀錄之後）：附註「已撤銷但未到期的保留，用來偵測重複使用」、保留規則「到期即清除」（沒有保留天數，不顯示「修改」；有效期依發出管道：App 由 `JwtSettings:RefreshTokenDays`、網頁由 `SessionSettings`〔0.10.8 起〕決定）、下次會清掉＝已到期筆數、負責作業「登入 Token 清理」（`RefreshSessionRetention`，可「立即清理」）、「前往」`/myusers`（要讓某人的 Token 立刻失效，在那裡強制登出）；沒有自訂清除。

## 四、內部系統運作

- **彙總** `Web/DataRetention/DataRetentionOverviewService`（scoped）：組合 `ScheduledJobOverviewService`、各資料服務的 `GetRetentionStatsAsync`、`BackupStore`、`INLogFilePathResolver` 與各保留設定（`IOptionsMonitor<T>.CurrentValue`，含系統參數覆寫）；每列各自 try/catch。
- **與清理同一個條件**：各服務把清理的 Where 抽成 `private static Expression<Func<T,bool>>`，清理與統計共用；門檻由 `Scheduling/RetentionCutoffs`（本地或 UTC 依資料表）或服務的 `ExpiryThreshold`（Token、AI 對話、Agent 對話：當天 0 點往前）計算，清理作業同步改用。
  - 站內通知兩條規則取 Id 聯集，不重複計。
  - Agent 對話依 `UpdatedAt`（最後一次對話），不是建立時間；統計與清理涵蓋全體使用者。
  - 已刪除資料的數字是「最多」：support、預設角色、仍在使用的角色、還有下屬的團隊會被清理留到下次。
  - 系統備份：`max(0, 份數 + 1 − KeepCount)`（下一次備份成功後刪除）。日誌檔：最後修改超過 `NLogRetention.MaxArchiveDays`（30，對照 nlog.config）的檔案數，約略值。
- **檔案空間** `DirectorySizeProbe`：遞迴加總，略過無權限與 reparse point；各資料夾在背景並行、整體 10 秒上限，超過或出錯顯示「至少 X」；已刪除專案的附件大小由資料庫 `ProjectFile.FileSize` 加總。
- **立即清理**：先重新計算一次（確認窗用最新數字）→ 紅色確認窗 → `ScheduledJobOverviewService.TryTrigger`（與排程作業頁「立即執行」同一個佇列與作業鎖）→ 稽核。作業停用或總開關關閉時仍可執行一次；保留規則為 0、已在佇列或執行中、找不到作業時停用按鈕並在提示說明原因。進行中每 5 秒自動更新。
- **自訂清除**：`CustomPurgeCutoff` 依 `RetentionTimeBasis` 換門檻（例外、已刪除資料用本地；稽核、通知、執行紀錄用 UTC；Token、AI 對話、Agent 對話以日計），「刪除某天之前」是那天本地 0 點。預覽用各服務的 `GetRetentionStatsAsync(門檻)`，刪除用同一個條件的明確門檻方法；已刪除資料經 `SoftDeletePurgeService.PurgeExpiredTypeAsync` 只清一種、保護規則不變；站內通知只依建立時間。Token 用量分批刪除（`PurgeBeforeBatchedAsync`，與自動過期共用）。
- **白話頻率** `Scheduling/CronDescriber`：每天／每週X／每月 d 日／每小時第 m 分，其他寫法原樣顯示 cron。

## 五、權限與安全

- 只限管理員；畫面不顯示任何紀錄內容，只有數量與時間。
- 稽核：「立即清理」寫 `Job.Trigger`（detail `job=…; source=data-retention; expected=N`），清理本身仍由各作業寫 `*.AutoPurge`。「自訂清除」有刪到資料時寫各自的 `*.Purge`（例外、Token、AI 對話、Agent 對話 `AiAgentConversation.Purge`、稽核、五種已刪除資料）、`Notification.Purge`、`Job.PurgeRuns`，detail `source=data-retention; mode=keepDays|beforeDate; value=…; rows=N`。

## 六、錯誤與邊界

- 設定驗證失敗（`OptionsValidationException`）：只影響讀那個設定的列，顯示「讀取失敗」；排程設定錯誤沿用排程作業頁的警示。
- 資料夾未設定顯示「未設定」、還不存在顯示「0 B」。
- 按下時排程剛好開始執行或規則剛被改成 0：提示「現在不能清理」，不放進佇列。

## 七、驗收與測試

- 統計與清理一致：`RetentionJobTests`（例外、稽核、Token、AI 對話，資料放在本地與 UTC 相差的 8 小時內；Agent 對話依最後一次對話時間）、`SoftDeletePurgeTests`（五種、附件大小）、`NotificationTests`（兩條規則不重複計）、`ScheduledJobFrameworkTests`（執行中的不算）。
- `CustomPurgeCutoffTests`（驗證、三種時間基準的換算）、`SoftDeletePurgeTests`（`PurgeExpiredTypeAsync` 只清一種、保護規則）。
- `DataRetentionOverviewServiceTests`（真正的主機：自訂清除對 9 種資料「預覽＝實際刪除」、已刪除資料只清那一種、巡檢報告／備份／日誌／公告沒有自訂清除；16 列順序、沒有讀取失敗、負責作業、備份與沒有作業的列沒有「立即清理」、五個頁面支援 `?deleted=1`、確認窗文字）、`CronDescriberTests`、`DirectorySizeProbeTests`、`NLogRetentionTests`。
- `MaintenanceApiTests`（0.10.6 起）：總覽每種都有、帶伺服器的「今天」、時間不帶時差；立即清理放進佇列並寫稽核、不認得的種類 404、沒有作業 400；自訂清除先預覽再刪除並寫稽核、不合法條件與不支援的種類 400；非管理員 403。
- 守門同步：`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`PageHelpCatalogTests`、`PageAuthorizationTests`、`LoggingConventionTests`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/DataRetention/`（`DataRetentionOverviewService`、`DirectorySizeProbe`）、`Scheduling/RetentionCutoffs.cs`、`Scheduling/CronDescriber.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/RetentionStats.cs` 與各服務的 `GetRetentionStatsAsync`
- `src/MyProject/MyProject.Web.Client/Views/Admins/DataRetentionView.razor(.cs)`（0.10.6 起 WASM）、`Api/MaintenanceApi.cs`（`DataRetentionApi`）、`MyProject.Web/Controllers/DataRetentionController.cs`、`Datas/Help/data-retention.md`
- 規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)「資料保留與清理」；相關 PRD：[排程作業](排程作業-prd.md)、[系統參數](系統參數-prd.md)、[系統備份與還原](系統備份與還原-prd.md)
