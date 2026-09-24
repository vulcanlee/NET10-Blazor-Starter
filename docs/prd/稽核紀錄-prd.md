# 稽核紀錄 PRD

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.9.59
- 首次實作版本：0.9.42
- 最後核對日期：2026/09/24

## 一、目標與範圍

`AuditLog` 從 0.4.8 起就在寫「誰、何時、對什麼、做了什麼、結果如何」，但一直**只有寫入端**：
有資料表、有服務、有測試，沒有畫面也沒有選單項。客戶問「誰把這個角色的權限改掉了」時，
只能直接開 SQLite 檔查。0.9.42 補上查詢畫面，讓這條軌跡拿得出來。

- 範圍：管理員專屬的稽核事件檢視／查詢／明細／CSV 匯出／清除與清空。
- 非範圍：**不提供 Web API**（內部管理頁）；不做告警通知；不做自動清除（一律手動）；
  不修改既有的稽核寫入點。
- 相關：寫入端的事件涵蓋範圍見 [使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、
  [登入與帳號流程](登入與帳號流程-prd.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/audit-logs` | 系統管理 → 權限管理 → id=35「稽核紀錄」 | **管理員專屬**（`CheckIsAdmin()`；權限鍵不上架角色矩陣）| 系統管理員、稽核人員 |

權限鍵常數為 `MagicObjectHelper.角色_稽核紀錄`，值 `"稽核紀錄"`。
與同群組的「使用者管理」「角色管理」一致，**刻意不列入** `RolePermissionService.GetRoleListPermissionAllName()`
—— 因此不會種出 `Permission` 資料列、任何角色都無法被授予，只有管理員短路能通過。
`AdminOnlyPermissionTests` 會擋下「順手補齊」的改動。

## 三、畫面與欄位

### 3.1 工具列

- 篩選：發生時間範圍（兩個 `DatePicker`）、動作下拉、結果下拉（不限／成功／失敗）、
  操作者帳號、關鍵字（比對動作／操作者／目標類型／目標識別／摘要）。
- **動作下拉的選項取自資料庫的 `SELECT DISTINCT Action`**，不寫死清單：動作代碼目前散落在
  15 個呼叫點的字串字面值裡，沒有集中的常數來源，任何手寫清單都會在下次新增稽核事件時默默過期。
- 動作（`ToolbarIconButton`）：查詢 `search`、重新整理 `refresh`、匯出 CSV `file_download`、
  清除 365 天前 `history`、清空全部 `delete_forever`。
- 兩個破壞性動作以 `ConfirmDialog.AskDestructiveAsync` 二次確認。
- 匯出的 CSV 為 **UTF-8 含 BOM**（走 `TextDownloadPayload.Utf8WithBom`，`ExportEncodingConventionTests` 守門）。

### 3.2 清單欄位

| 欄位 | 說明 |
| --- | --- |
| 發生時間 | 可排序，預設遞減。**顯示為伺服器本地時間**（資料庫存 UTC）|
| 結果 | 成功綠／失敗紅的 `Tag` |
| 動作 | 動作代碼，依第一節（`Login`／`User`／`Role`／`Permission`／`Audit`）上色 |
| 操作者 | 帳號（Id=N）；系統或匿名事件顯示「（系統／匿名）」|
| 目標 | `TargetType#TargetId`，兩者皆空顯示破折號 |
| 摘要 | `Detail`，過長以 `Ellipsis` 截斷 |
| 操作 | 「查看」開明細窗 |

### 3.3 明細窗

列出本地時間、**UTC 原值**、結果、動作代碼、操作者、目標類型、目標識別與完整摘要。
UTC 原值一併呈現是刻意的：跨時區協查或要與日誌檔的時間戳對照時會用到。

尺寸與內容樣式寫在 `Components/Commons/OverlayStyles.razor` 的 `.audit-log-modal`，
**不是** `AuditLogView.razor.css` —— AntDesign 的 `Modal` 渲染在元件 DOM 之外，
scoped CSS 連 `::deep` 都打不到。

## 四、⚠️ 時區：本頁最關鍵的設計

`AuditLog.OccurredAt` 是**全專案唯一以 UTC 寫入**的時間欄位（`AuditLogService` 用 `DateTime.UtcNow`，
與帳號鎖定的 `LockoutEndUtc` 一致）。相對地，`ExceptionLog` 與 `TokenUsageLog` 寫的都是
`DateTime.Now`（本地時間），所以那兩頁全程不做換算。

**照抄那兩頁會讓整條時間軸偏掉一個時區（台灣 8 小時）。** 換算集中在 `AuditLogQueryService`：

| 邊界 | 方向 | 作法 |
| --- | --- | --- |
| DatePicker → 查詢條件 | 本地 → UTC | `AuditLogQueryService.ToUtc`（`SpecifyKind(Local).ToUniversalTime()`）|
| 資料庫 → 清單／明細／CSV | UTC → 本地 | `AuditLogQueryService.ToLocal`（`SpecifyKind(Utc).ToLocalTime()`）|
| 清除門檻 | — | `DateTime.UtcNow.AddDays(-days)`，**不是** `DateTime.Now` |

`AuditLogAdapterModel.OccurredAt` 拿到時**已經是本地時間**，呼叫端不可以再轉一次。

`AuditLogQueryServiceTests` 有三條測試釘住這三個邊界；把換算拿掉會立刻轉紅（已實測）。

**已知限制**：Blazor Server 的 `ToLocalTime()` 取的是**伺服器**時區，不是瀏覽器時區。
與 `SystemHealthPage`、日誌檢視、例外紀錄、Token 用量四頁的口徑一致，刻意不另做 JS interop。

## 五、清除與保存

- 「清除 365 天前」門檻是程式常數 `AuditLogView.PurgeDays`，不是設定鍵。
  取 365 天而非例外紀錄的 90 天：例外紀錄清的是噪音，稽核軌跡清的是責任證據。
- **清除與清空動作本身會各寫一筆稽核紀錄**（`Audit.Purge` / `Audit.ClearAll`，含操作者與筆數）。
  寫入排在刪除之後，所以那一筆會留在清空後的資料表裡。
  ⚠️ 這是刻意的：稽核軌跡可被管理員一鍵抹除，若不記錄，「誰抹除了稽核紀錄」會是系統裡唯一查不到的事。
- 稽核寫入失敗只記錯誤、不向使用者報錯 —— 不應推翻「已經刪除」這個事實。

## 六、資料來源

寫入端共 17 個呼叫點，約 13 種動作代碼：

| 動作代碼 | 來源 |
| --- | --- |
| `Login.Success` / `Login.Failed` / `Login.Disabled` / `Login.LockedOut` | `MyUserServiceLogin` |
| `User.Create` / `User.Update` / `User.Delete` | `MyUserService` |
| `Role.Create` / `Role.Update` / `Role.Delete` | `RoleViewService` |
| `Permission.Denied` | `HasPermissionAttribute`（API 動作級授權被拒）|
| `Project.FileDownload` | `ProjectFileController` |
| `LogLevel.Apply` / `LogLevel.Restore` | `LogLevelSettingView` |
| `LogViewer.AiAnalyze` / `LogViewer.AiAnalyzeExportPdf` | `LogViewerView` |
| `Audit.Purge` / `Audit.ClearAll` | `AuditLogView`（0.9.42 起）|
| `Email.Test` | `EmailTestService`（系統健康監控頁的寄信測試，0.9.59 起；detail 只有 provider 與成敗，不含收件者）|

## 七、已知限制與規劃中需求

**已知限制**

1. **動作代碼沒有單一來源**：15 個呼叫點各自寫字串字面值，沒有 `AuditActions` 常數類別，
   也沒有守門測試。新增寫入點時若打錯字，只會在查詢頁多出一個孤兒代碼。
   下拉選單改讀資料庫 DISTINCT 正是為了不被這件事拖累。
2. **`AuditLog.OccurredAt` 沒有索引**：`ExceptionLog.LastOccurredAt` 與 `TokenUsageLog.OccurredAt`
   在 `BackendDBContext` 都建了索引，`AuditLog` 沒有。資料量大到影響查詢時應補一支 migration。
3. 時區以伺服器為準（見 §四）。
4. 清除與清空沒有保留期限的自動化 —— 專案目前沒有任何排程基礎建設。

**規劃中需求**（不屬於目前驗收範圍）

- 動作代碼常數化與守門測試。
- `OccurredAt` 索引 migration。
- 稽核事件的告警（例如同一帳號短時間大量 `Login.Failed`）。

## 八、測試

- `AuditLogQueryServiceTests`（24 項）：時區三條、過濾、排序、同秒分頁不重複、
  總筆數、下拉去重排序、清除門檻以 UTC 計算、清空。
- `AdminOnlyPermissionTests`：`角色_稽核紀錄` 不得出現在角色矩陣。
- `MenuPermissionConsistencyTests`：`AuditLogView.razor.cs` 列於 `AdminOnlyViews`，
  不得使用 `CheckAccessPage`。
- `MenuIconTests`：`fact_check` 已列入允許的 classic Material Icons。
- `DataAccessServiceLifetimeTests`：`AuditLogQueryService` 必須注入 `IDbContextFactory`。
- `PageAuthorizationTests`：`/audit-logs` 自動納入受保護路由掃描。

> 返回 [PRD 主控台](README.md)
