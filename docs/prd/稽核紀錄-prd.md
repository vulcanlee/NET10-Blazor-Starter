# 稽核紀錄 PRD

- 文件版本：1.33
- 文件狀態：已實作
- 現行系統版本：0.10.22
- 首次實作版本：0.9.42
- 最後核對日期：2026/11/14

## 一、目標與範圍

`AuditLog` 從 0.4.8 起就在寫「誰、何時、對什麼、做了什麼、結果如何」，但一直**只有寫入端**：
有資料表、有服務、有測試，沒有畫面也沒有選單項。客戶問「誰把這個角色的權限改掉了」時，
只能直接開 SQLite 檔查。0.9.42 補上查詢畫面，讓這條軌跡拿得出來。

- 範圍：管理員專屬的稽核事件檢視／查詢／明細／CSV 匯出／清除與清空；0.9.173 起另有五個資料頁每列的「異動歷程」（§3.4）；0.9.175 起每筆紀錄加上防竄改簽章，可驗證完整性（§九）。
- Web API：0.10.2 起先有「異動歷程」對話窗用的兩個管理員專用端點（§3.4）；0.10.4 起（WASM 遷移 M5）本頁改以 WebAssembly 執行，查詢、選項、明細、匯出、清除、清空與完整性驗證都有管理員專用端點（`api/v1/audit-logs`，見 §3.5 與 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)）。
- 非範圍：不做告警通知；
  不修改既有的稽核寫入點。
- 相關：寫入端的事件涵蓋範圍見 [使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、
  [登入與帳號流程](登入與帳號流程-prd.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
| --- | --- | --- | --- |
| `/audit-logs` | 系統管理 → 帳號與權限 → id=35「稽核紀錄」 | **管理員專屬**（0.10.4 起 WASM 頁以 `PagePermissions.IsAdmin` 判斷、API 以 `[AdminOnly]` 擋；之前是 `CheckIsAdmin()`；權限鍵不上架角色矩陣）| 系統管理員、稽核人員 |

0.10.4 起頁面在 WebAssembly 執行（`MyProject.Web.Client/Pages/AuditLogPage.razor`、`Views/Admins/AuditLogView.*`，版面 `ClientMainLayout`；Server 版已刪除），畫面、按鈕、對話窗與訊息和之前相同。
非管理員開頁顯示「你沒有權限存取此頁面」，`Permission.Denied` 由伺服器在 `POST /api/v1/shell/page-view` 寫；直接呼叫 API 回 403，並寫一筆 `Permission.Denied`（detail「需要管理員」）。

權限鍵常數為 `MagicObjectHelper.角色_稽核紀錄`，值 `"稽核紀錄"`。
與同群組的「使用者管理」「角色管理」一致，**刻意不列入** `RolePermissionService.GetRoleListPermissionAllName()`
—— 因此不會種出 `Permission` 資料列、任何角色都無法被授予，只有管理員短路能通過。
`AdminOnlyPermissionTests` 會擋下「順手補齊」的改動。

## 三、畫面與欄位

### 3.1 工具列

- 篩選：發生時間範圍（兩個 `DatePicker`）、動作下拉、結果下拉（不限／成功／失敗）、
  目標類型下拉、目標識別（精確）（0.9.173 起，見下）、
  操作者帳號、來源 IP（0.9.123 起，前綴比對，沒有 IP 的舊紀錄不符合；匯出 CSV 同條件）、關鍵字（比對動作／操作者／目標類型／目標識別／摘要／來源 IP，0.9.117 起含 IP；部分比對）。
- **精確查詢（0.9.173 起）**：放在「結果」之後、「操作者帳號」之前。
  - 「目標類型」下拉第一項「目標類型：不限」，其餘取自 `AuditLogQueryService.GetDistinctTargetTypesAsync`（`SELECT DISTINCT TargetType`，排除 null 與空字串，排序），與動作下拉同時載入，按「重新整理」更新；選中的值已不在選項裡時清回不限。
  - 「目標識別（精確）」輸入框：`AuditLogQuery.TargetId`，去前後空白後**完全相同**才符合。原因：關鍵字是 `Contains`，查 `12` 會混到 `112`、`120`，追某一筆資料時不能用。
  - 兩欄各自獨立，可只填一個；建議一起填（不同類型可能有相同編號）。查詢有索引 `IX_AuditLog_TargetType_TargetId_OccurredAt`（§七）。
  - 匯出 CSV 套用同樣條件；按「查詢」寫的 `Audit.Search` 多記 `targetType`（下拉值，摘要直接記）與 `targetId`（自由輸入，摘要只記有沒有填、原文在內容檔）。
- **動作下拉的選項取自資料庫的 `SELECT DISTINCT Action`**，不寫死清單：0.9.42 立頁時動作代碼散落在
  15 個呼叫點的字串字面值裡，沒有集中的常數來源，任何手寫清單都會在下次新增稽核事件時默默過期
  （0.9.78 起已收斂到 `AuditActions`，下拉仍讀 DISTINCT，見 §七）。
- 動作（`ToolbarIconButton`）：查詢 `search`、重新整理 `refresh`、匯出 CSV `file_download`、
  清除超過保留天數 `history`（0.9.174 起提示文字「清除超過保留天數的紀錄（異動 N 天、瀏覽 M 天）」，見 §五）、清空全部 `delete_forever`、驗證完整性 `verified_user`（0.9.175 起，提示文字「驗證完整性（檢查有沒有被改、被刪）」，見 §九）。匯出 CSV 0.9.78 起寫一筆 `Audit.Export` 稽核。
- 頁首藍色說明框（0.9.174 起）：`AuditLogSettings` 的開關有關閉時多一行「目前沒有記錄修改前後差異與瀏覽類事件（換頁、搜尋、查看），可在「系統參數」頁開啟。」，只關一個時只列那一個，都開時不顯示（§六）。
- 兩個破壞性動作以 `ConfirmDialog.AskDestructiveAsync` 二次確認。
- 匯出的 CSV 為 **UTF-8 含 BOM**（走 `TextDownloadPayload.Utf8WithBom`，`ExportEncodingConventionTests` 守門）。0.10.4 起由伺服器產生（`POST api/v1/audit-logs/export`），欄位定義從頁面搬到 `MyProject.Web/Export/AuditLogExport.cs`（欄位、順序與檔名不變）。

### 3.2 清單欄位

| 欄位 | 說明 |
| --- | --- |
| 發生時間 | 可排序，預設遞減。**顯示為伺服器本地時間**（資料庫存 UTC）。0.10.4 起點標題依點選方向排序；之前 Server 版把 AntDesign 的方向字串（`Ascending`／`Descending`）拿去比對 `ascend`／`descend`，永遠對不上，點了順序不會變 |
| 結果 | 成功綠／失敗紅的 `Tag` |
| 動作 | 動作代碼，依第一節（`Login`／`User`／`Role`／`Permission`／`Audit`，以及後來加入的 `Project`／`Category`／`Team`／`Password`／`Backup` 等各類；未列出的為預設色）上色 |
| 操作者 | 帳號（Id=N）；系統或匿名事件顯示「（系統／匿名）」|
| 來源 IP | `ClientIp`（0.9.117 起）；排程作業與舊紀錄顯示「—」。CSV 加在最後一欄「來源IP」|
| 目標 | `TargetType#TargetId`，兩者皆空顯示破折號 |
| 摘要 | `Detail`，過長以 `Ellipsis` 截斷 |
| 操作 | 「查看」開明細窗 |

### 3.3 明細窗

列出本地時間、**UTC 原值**、結果、動作代碼、操作者、來源 IP、目標類型、目標識別、簽章（0.9.175 起）與完整摘要。
「簽章」顯示「已簽章，異動鏈 #6」（鏈別＋鏈內序號）或「升級前的紀錄，未納入完整性驗證」（0.9.175 前寫入、沒有簽章的舊紀錄）。
UTC 原值一併呈現是刻意的：跨時區協查或要與日誌檔的時間戳對照時會用到。
0.10.4 起本地與 UTC 時間都由伺服器算好送來（`GET api/v1/audit-logs/{id}`），瀏覽器時區不同也不會顯示錯（見 §四）。

**詳細內容（0.9.131 起）**：有內容檔的紀錄在摘要下方多一個「詳細內容」區塊 —— 修改的前後差異（`欄位: 舊值 → 新值`）
或查詢的完整條件（含搜尋原文）。內容檔存在 `SystemSettings:ExternalFileSystem:AuditContentPath`（`{yyyyMM}/{Guid}.txt`，月份依 UTC），
資料表只存相對路徑 `ContentFile`；清單查詢不讀檔，開明細窗時才經 `AuditLogQueryService.GetContentAsync` 讀取，檔案不在時顯示說明。
內容檔不進 CSV、不進日誌；清除 N 天前、清空全部與自動清理都會連同內容檔一起刪（`AuditContentFileStore` 是唯一入口），系統備份也會打包（`files/AuditContent`）。

尺寸與內容樣式寫在 `MyProject.Web.Client/Commons/OverlayStyles.razor` 的 `.audit-log-modal`，
**不是** `AuditLogView.razor.css` —— AntDesign 的 `Modal` 渲染在元件 DOM 之外，
scoped CSS 連 `::deep` 都打不到。

### 3.4 異動歷程（0.9.173 起）

**問題**：「這一筆分類是誰改的、改了什麼」是最常被問的稽核問題，但要先記下編號、切到稽核紀錄頁、再用關鍵字查 —— 而關鍵字的部分比對會把 12 和 112 混在一起。
**做法**：在資料所在的清單上直接看那一筆的異動。

- **位置**：分類清單 `/categories`、專案項目 `/projects`、團隊清單 `/teams`、使用者管理 `/myusers`、角色管理 `/roleviews` 每列操作欄的最後一顆「異動歷程」（`CrudActionButton`，圖示 `history`），一般清單與「顯示已刪除」兩個分支都有（已刪除的資料更需要查是誰刪的）。`New-CrudModule.ps1` 產生的頁面也有，目標類型＝模組名稱。
- **權限：只有管理員**。稽核紀錄含來源 IP 與別人的操作，與本頁同樣只給管理員、權限鍵不上架角色矩陣。`AuditHistoryButton` 以 `PagePermissions.IsAdmin`（0.10.2 起；之前是 `CurrentUser.IsAdmin`，規則相同）決定渲染（非管理員連 `SpaceItem` 都不留，分類／專案／團隊頁的一般使用者也看不到），`AuditHistoryDialog.ShowAsync` 再擋一次並記警告日誌。
- **元件**：`MyProject.Web.Client/Commons/AuditHistoryButton.razor`、`AuditHistoryDialog.razor(.cs/.css)`（0.10.2 起放在 Client，命名空間仍是 `MyProject.Web.Components.Commons`；0.10.8 以前 Server 與 WASM 頁面共用）；一頁只放一個對話窗，由按鈕呼叫 `ShowAsync(targetType, targetId, label)`。`targetType` 必須與服務寫稽核的 `TargetType` 相同（`Category`、`Project`、`Team`、`MyUser`、`RoleView`），否則永遠是空的 —— 由 `AuditHistoryConventionTests` 守門。
- **資料來源**（0.10.2 起，`IAuditHistorySource`）：0.10.9 起只有 WASM 頁面（0.10.8 以前 Server 頁面用 `ServerAuditHistorySource` 直接查 `AuditLogQueryService`，已刪除）；WASM 頁面（分類、團隊，0.10.3 起加上專案，0.10.4 起加上使用者、角色，五個資料頁都是）用 `ApiAuditHistorySource`（`MyProject.Web.Client/Api/`），呼叫管理員專用的 `AuditLogsController`：
  `GET /api/v1/audit-logs/history?targetType=&targetId=&page=&pageSize=&opened=`（每頁 1～50 筆；`opened=true` 是剛打開，寫一筆 `Audit.Search`，換頁不寫）與 `GET /api/v1/audit-logs/{id}/content`（詳細內容，寫一筆 `Audit.View`）；非管理員回 403。
  兩種來源寫的稽核與錯誤文字相同；API 不回傳伺服器上的內容檔路徑，只回 `HasContent`。API 回錯誤或連不上伺服器時，右下角顯示 API 的錯誤訊息（預設「載入異動歷程失敗。」）。
- **標題**：「異動歷程：<名稱>」，使用者頁顯示帳號（不顯示姓名）、專案頁顯示 `Title`、其他顯示 `Name`。上方說明：「這一筆資料（<類型> #<編號>）在稽核紀錄裡的異動，新的在上面；時間為伺服器本地時間。稽核紀錄保留期限之前的異動已被清除。要看誰開過、查過這一筆，請到「稽核紀錄」頁查詢。」
- **左側清單**：`AuditLogQueryService.GetTargetHistoryAsync(targetType, targetId, page, 10)` —— 目標類型＋目標識別精確比對，**排除瀏覽類**（`Page.View`、以 `.Search` 或 `.View` 結尾；SQL 版本是共用的 `NotBrowsing` 條件，與 `AuditActions.IsBrowsing` 同一規則，也給 `AuditLogQuery.ExcludeBrowsing` 用），依 `OccurredAt`、`Id` 遞減，時間轉本地。每筆顯示時間、動作代碼 `Tag`（顏色同本頁）、失敗多一個紅色「失敗」、操作者「帳號（Id=N）」；超過 10 筆出現小型分頁。目標是 `MyUser` 的密碼重設、兩步驟驗證、解鎖、強制登出、本人改姓名等也會列出；登入事件沒有目標，不列。
- **右側明細**：點一筆後顯示發生時間、動作代碼、結果、操作者、來源 IP、摘要（空的「（無）」）、詳細內容（點選才經 `GetContentAsync` 讀檔）。沒有內容檔：「（這一筆沒有詳細內容。修改前後差異只在「修改」、而且系統參數「稽核：記錄修改前後差異」開啟時才會記錄）」（0.9.174 起改字）；檔案不在：「內容檔不存在（可能已被清除，或當初寫檔失敗）。」；讀檔失敗：「讀取詳細內容失敗。」附追蹤碼。還沒選時：「請從左側選一筆，查看摘要與修改前後差異。」
- **空狀態與錯誤**：沒有任何異動：「這一筆資料在稽核紀錄的保留期間內沒有異動紀錄。」；載入失敗：右下角 `ViewNotification.UnexpectedError`「載入異動歷程失敗：<例外類型>。」
- **稽核**：打開寫 `Audit.Search`（`TargetType=AuditLog`，摘要 `targetType=…; targetId=…; via=history`）；點一筆寫 `Audit.View`（`TargetId` 是那一筆稽核的 Id，摘要 `action=…; via=history`）。這兩筆屬瀏覽類，不會出現在任何資料的異動歷程裡；0.9.174 起只在 `AuditLogSettings:RecordBrowsing` 開啟時才寫。
- **尺寸**：`OverlayStyles.razor` 的 `.audit-history-modal`（86vw、max-width 1280px、高 88vh，body 不捲、清單與明細各自捲動）；窗內樣式在 `AuditHistoryDialog.razor.css`。純唯讀、不含 `EditForm`，× 或 Esc 關閉。
- **為什麼排除瀏覽類**：0.9.131 起每次按「修改」開窗都會記 `*.View`，熱門資料一天幾十筆，會把真正的異動淹掉。要看「誰開過、查過」請到本頁用目標類型＋目標識別查（`*.View` 的目標就是那一筆）。

⚠️ 異動歷程靠動作代碼的結尾判斷瀏覽類，所以**瀏覽類動作的常數必須叫 `Search`／`View`，改了資料的動作值不可以 `.Search`／`.View` 結尾**（`AuditConventionTests.IsBrowsing_ShouldBeTrueExactlyForSearchAndViewConstants` 守門，CRUD 產生器的新模組自動相容；C# 與 SQL 兩份規則由 `AuditLogQueryServiceTests.NotBrowsing_ShouldAgreeWithIsBrowsing_ForEveryActionCode` 對每個動作代碼比對；速查表 §6.6）。

**`AuditLogQuery.ExcludeBrowsing`**（0.9.173 起）：查詢條件可排除瀏覽類，規則同上。稽核紀錄頁沒有這個選項（頁面上要看得到誰開過、查過）；系統管理助理的 `search_audit_logs` 在給了目標、沒指定動作時自動打開它（見 [系統管理助理 PRD](系統管理助理-prd.md)）。

### 3.5 Web API（0.10.4 起，WASM 遷移 M5）

`AuditLogsController`（`api/v1/audit-logs`，JWT Bearer），每個 action 都標 `[AdminOnly]`：非管理員回 403 並寫 `Permission.Denied`（detail「需要管理員」）。稽核的寫法與 Server 版相同，WASM 頁面只經這些端點取資料：

| 端點 | 用途 | 寫的稽核 |
| --- | --- | --- |
| `POST search` | 清單（條件同 §3.1，分頁） | `RecordSearch=true`（使用者按「查詢」）時寫 `Audit.Search`；自由輸入的欄位（操作者帳號、來源 IP、關鍵字、目標識別）摘要只記有沒有填 |
| `GET options` | 資料庫出現過的動作與目標類型、清除的保留天數（異動、瀏覽）、記錄範圍提示 | — |
| `GET {id}` | 明細與詳細內容；本地與 UTC 時間由伺服器算（§3.3） | `Audit.View` |
| `POST export` | CSV（UTF-8 含 BOM，只匯前 10000 筆、與 Server 版相同，條件同搜尋） | `Audit.Export`：detail 記實際匯出筆數（`format=csv; rows=N`），超過 10,000 筆被截掉時另記總筆數（`format=csv; rows=10000; total=N`） |
| `POST purge` | 清除超過保留天數（先異動與安全、成功後再瀏覽） | 兩段各一筆 `Audit.Purge` |
| `POST clear-all` | 清空全部 | `Audit.ClearAll` |
| `POST verify` | 完整性驗證（§九），只比對、不更新檢查點 | `Audit.Verify`（`trigger=manual`） |
| `GET history`、`GET {id}/content` | 異動歷程（0.10.2 起，§3.4） | `Audit.Search`／`Audit.View`（`via=history`） |

驗證完整性的時間與紀錄筆數成正比，WASM 端這個請求最長等 30 分鐘（`ApiClient.PostLongRunningAsync`），不受一般請求 100 秒的逾時限制。
Controller 另套 `[ApiValidationFilter]`：每頁筆數 1～200、頁碼 ≥ 1，不合法回 400。

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

**已知限制**：本地時間由伺服器換算（0.10.8 以前是 Blazor Server 的 `ToLocalTime()`），取的是**伺服器**時區，不是瀏覽器時區。
與系統健康監控（0.10.6 起 `SystemHealthView`）、日誌檢視、例外紀錄、Token 用量四頁的口徑一致，刻意不另做 JS interop。

**WASM（0.10.4 起）**：換算仍全在伺服器做，口徑不變（伺服器時區）；WASM 頁面不在瀏覽器裡再換算，明細的 UTC 時間也由伺服器算好送來，瀏覽器時區不同也不會偏。
⚠️ 本地時間（清單、明細、「異動歷程」對話窗）一律以**不帶時差**（`DateTimeKind.Unspecified`）送出，UTC 欄位以 `Z` 結尾：之前帶 `+08:00`，WASM 反序列化時會換成瀏覽器的時區，伺服器與瀏覽器時區不同時整頁時間都錯（獨立審查發現）。查詢條件的起訖時間在 WASM 端送出前也先去掉時區（`ClientCulture.Unzoned`），伺服器照自己的時區解讀。
⚠️ `AuditLogQueryService.GetByIdAsync` 單筆查詢回的是資料庫的 UTC（只有清單會換成本地），`AuditLogsController.Detail` 先 `ToLocal` 再回傳 —— 實機曾發現明細被當成本地再換算一次，差了 8 小時。

## 五、清除與保存

0.9.174 起稽核分兩類保留（`AuditLogKind`：`All`／`Change`／`Browsing`，瀏覽類由 `AuditActions.IsBrowsing` 判斷，沒有加欄位、沒有 migration）：

| 類別 | 範圍 | 保留天數 | 預設 |
| --- | --- | --- | --- |
| 異動與安全（`Change`） | 瀏覽類以外的全部：登入、權限拒絕、新增、修改、刪除、清除… | `LogRetentionSettings:AuditLogDays` | 365 |
| 瀏覽類（`Browsing`） | `Page.View`、`*.Search`、`*.View` | `LogRetentionSettings:AuditBrowsingLogDays`（0～36500，0＝不自動清除） | 90 |

- 預設 365 天而非例外紀錄的 90 天：例外紀錄清的是噪音，稽核軌跡清的是責任證據。瀏覽類筆數最多、證據價值較低，所以另給較短的 90 天。
- **頁面清除按鈕**：提示文字「清除超過保留天數的紀錄（異動 N 天、瀏覽 M 天）」，N 讀 `ManualAuditLogDays`（`AuditLogDays`，0 時退回 365）、M 讀 `ManualAuditBrowsingLogDays`（`AuditBrowsingLogDays`，0 時退回 90）。
  確認窗標題「清除超過保留天數的紀錄」，內文「將刪除早於 N 天前的異動與安全紀錄，以及早於 M 天前的瀏覽紀錄（換頁、搜尋、查看）。此動作無法復原，且本次清除會另外留下稽核紀錄。」
  按「清除」後 `PurgeAsync(N, Change)`、`PurgeAsync(M, Browsing)` 各一次，各寫一筆 `Audit.Purge`；通知「異動與安全紀錄：<結果>瀏覽紀錄：<結果>」，每段是「已清除 N 筆紀錄。」或「沒有符合條件的紀錄。」。
  0.9.78～0.9.173 是單一門檻「清除 N 天前的紀錄」（N 讀 `AuditLogDays`，0.9.78 前為程式常數 365）。
- **自動清理**（0.9.78 起）：排程作業「稽核紀錄清理」（`AuditLogRetentionJob`，0.9.96 起預設每天 03:00；之前是 `LogRetentionWorker` 啟動時與每日）。
  0.9.174 起分兩段：異動與安全用 `AuditLogDays`、瀏覽類用 `AuditBrowsingLogDays`，各自 0 就略過；每段刪到資料各寫一筆 `Audit.AutoPurge`（操作者顯示「（系統／匿名）」，摘要 `rows=N; days=D; kind=change|browsing; trigger=T`）。
  結果訊息「刪除 N 筆超過 D 天的異動與安全紀錄；刪除 M 筆超過 E 天的瀏覽紀錄。」（某一類為 0 時那一段改為「…不自動清除（保留天數為 0）」）；兩類都 0 時「保留天數都設為 0，不自動清除。」。有法規或內控保存要求時請調大天數或設 0。
- ⚠️ **內容檔月份目錄**：`PurgeBeforeAsync(threshold, kind)` 只刪該類資料列各自的內容檔；空的月份目錄與孤兒檔由 `DeleteContentMonthsBeforeAsync` 收，而且**只在兩類天數都大於 0 時、用較舊的門檻**收；0.9.175 起邊界取「門檻」與「仍有內容檔的紀錄中最早的時間」較早者，還在的紀錄不會被收掉內容檔（否則完整性檢查會報「內容檔不見了」）。只清單一類時不收：用 90 天門檻收會誤刪 90～365 天前的修改差異檔（速查表 §6.30）。
- 「資料保留與清理」頁：稽核那一列的總筆數、最舊一筆與「下次會清掉」是兩類合計（`GetRetentionStatsAsync(threshold, kind)` 各算一次）；「自訂清除」不分類（`AuditLogKind.All`）。
- **清除只刪每條雜湊鏈最舊的一段**（0.9.175 起）：每條鏈的保留起點＝門檻之後最早的那一筆，只刪序號比它小的，並在同一個交易裡把鏈頭的起點往前推，所以正常清除不會被當成竄改；序號和時間偶爾交錯時寧可少刪一兩筆。統計與清除共用 `AuditChainStore.ResolveExpiredAsync`（§九）。
- **清空全部**（0.9.175 起）：確認窗內文「將刪除所有稽核紀錄。此動作無法復原，且本次清空會另外留下一筆稽核紀錄；完整性驗證會從下一筆紀錄重新起算。」兩條鏈的序號**接著編、不歸零**，鏈頭起點移到下一筆；之前累積的被改、缺號等問題也一起清掉。
- **清除與清空動作本身會寫稽核紀錄**（`Audit.Purge`〔0.9.174 起每一類一筆〕/ `Audit.ClearAll`，含操作者與筆數）。
  寫入排在刪除之後，所以那一筆會留在清空後的資料表裡。
  ⚠️ 這是刻意的：稽核軌跡可被管理員一鍵抹除，若不記錄，「誰抹除了稽核紀錄」會是系統裡唯一查不到的事。
- 稽核寫入失敗只記錯誤、不向使用者報錯 —— 不應推翻「已經刪除」這個事實。

## 六、資料來源

0.9.78 起所有動作代碼集中在 `MyProject.Business/Helpers/AuditActions.cs`（0.9.78 時 59 個，現行 142 個，以 `AuditActions` 為準），呼叫點一律引用常數，
由 `AuditConventionTests` 守門；0.9.78 新增的事件（分類／團隊／專案增刪改、附件、登出、SSO、自行改密碼、JWT 刷新失敗、
Blazor 頁面權限拒絕、各頁匯出與維護、自動清理）見 [日誌與例外處理 PRD](日誌與例外處理-prd.md) §6.3 LOG-14。
**來源 IP（0.9.117 起）** 不由呼叫端傳入：`AuditLogService` 寫入時向 `IClientIpProvider` 取，
實作 `Web/Auth/ClientIpProvider` 依情境決定 —— Web API 讀 `HttpContext.Connection.RemoteIpAddress`（0.10.9 起所有頁面都是 WASM，含登入、登出、兩步驟驗證的操作都經 Web API，記該次請求的 IP）；
0.10.8 以前 Blazor Server circuit 由 `ApplicationCircuitHandler.OnConnectionUpAsync` 在連線建立與重連時寫入，0.10.9～0.10.21 工作流程執行登錄表以 `SetCircuitClientIp` 寫入（0.10.22 起隨工作流程移除）；
排程作業沒有 HttpContext，存 null。IPv4-mapped IPv6 轉回 IPv4。部署在反向代理後面時必須設定 `ForwardedHeaders`，否則記到的是代理的 IP。

**記錄範圍：可設定、預設關閉（0.9.174 起）**。0.9.131～0.9.173 一律記錄下表的瀏覽類事件與修改前後差異；0.9.174 起由 `AuditLogSettings` 兩個開關決定，**預設都關**，可在「系統參數」頁「資料保留」分類即時切換（改了寫 `SystemParameter.Update`）：

| 設定鍵 | 預設 | 系統參數名稱 | 生效 | 關閉時 |
| --- | --- | --- | --- | --- |
| `AuditLogSettings:RecordBrowsing` | false | 稽核：記錄瀏覽類事件 | 下一次換頁或搜尋起 | 瀏覽類（`Page.View`、`*.Search`、`*.View`，**含 `Audit.Search`／`Audit.View`＝誰看過稽核紀錄**）整筆不寫：不寫資料列、不寫內容檔，也不記 NLog Debug 行 |
| `AuditLogSettings:RecordFieldChanges` | false | 稽核：記錄修改前後差異 | 下一次修改起 | `*.Update` 與 `User.ProfileUpdate`（`AuditActions.CarriesFieldDiff`）照寫資料列，只丟掉內容檔（差異）；其他動作帶的內容（例如 `AiCallLog.Export` 匯出了哪些 Id）不受影響 |

- 登入、權限拒絕、新增、修改、刪除等安全與異動事件**永遠照記**。
- 判斷只在 `AuditLogService.WriteAsync` 一處，呼叫端照常傳 `content`；每次寫入讀 `IOptionsMonitor<AuditLogSettings>.CurrentValue`，切換立即生效；讀設定失敗時當作全關，但異動事件照寫。設定類別 `MyProject.Models/Systems/AuditLogSettings.cs`，已註冊 `ValidateOnStart`。
- 預設關閉的理由：瀏覽類是稽核表膨脹的主因，差異可能含個人資料；兩者都屬「需要時再開」。
- ⚠️ 瀏覽開關關閉時，資料頁「異動歷程」的打開與點選、系統管理助理查稽核，也都不會留下紀錄。

開關開啟時，除了改變狀態的動作與安全事件，也記使用者**開了哪些頁、查了什麼、看了哪一筆、改了什麼**：

| 動作代碼 | 來源與內容 |
| --- | --- |
| `Page.View` | `ShellController.PageView`（0.10.9 起唯一來源；WASM 外框 `ClientMainLayout` 每次換頁呼叫 `POST api/v1/shell/page-view`，只收 WASM 路徑、每人每分鐘 60 筆；0.10.8 以前 Server 頁由 `ApplicationCircuitHandler` 寫）：已登入使用者每換一頁一筆（只改 query string 不算）；`TargetId` 是路徑（不含 query string），Detail `from=上一頁`（第一頁為 `from=entry`）|
| `Category.Search` / `Project.Search` / `Team.Search` / `User.Search` / `Role.Search` | 五個主要資料頁按「搜尋」（翻頁、排序、清除不記）；Detail 只有短摘要（`hasKeyword=…; showDeleted=…`），完整條件含搜尋原文在內容檔 |
| `Category.View` / `Project.View` / `Team.View` / `User.View` / `Role.View` | 五個主要資料頁按「修改」開窗（查看那一筆）；Detail 是名稱或帳號 |
| `LogViewer.Search` / `ExceptionLog.Search` / `AiCallLog.Search` / `TokenUsage.Search` / `Audit.Search` | 管理診斷頁按「查詢」（日誌檢視打開頁面時的自動查詢不記）；條件同上 |
| `ExceptionLog.View` / `AiCallLog.View` / `TokenUsage.View` / `Audit.View` | 開明細窗。日誌檢視的展開只發生在瀏覽器端，沒有 `LogViewer.View`。0.10.5 起這四頁的查詢與明細稽核都由各自的 API（`ExceptionLogsController`、`AiCallLogsController`、`TokenUsageController`、`AuditLogsController`；日誌檢視是 `LogsController`）寫，規則不變 |

**修改前後差異**（0.9.174 起要開 `RecordFieldChanges`）：分類、專案、團隊、使用者、角色的修改（Blazor 與 Web API 兩條路徑；0.10.2 起分類、團隊的 API 也走服務，與畫面同一份）以及個人資料改姓名，
由 `AuditDiff` 比對修改前後的純量欄位寫進內容檔；Detail 維持原本的短摘要。主鍵、版本號、建立／更新時間、軟刪除欄位不比；
使用者的密碼只記「已變更」，Salt、SecurityStamp、兩步驟密鑰、Google 綁定、鎖定與失敗次數不比；額外角色、團隊、角色權限與預設團隊列出新增與移除。

**同步到日誌**：`AuditLogService` 每寫一筆就記一行 Debug `Audit recorded. Action=…, Detail=…, ContentFile=…`（只有短摘要與檔名，不含內容檔）；因瀏覽開關關閉而沒寫進資料庫的事件不記（0.9.174）。
Debug 預設不寫進檔案，要到「日誌等級設定」調到 Debug 才看得到。

下表為 0.9.42 立頁時的寫入端：

| 動作代碼 | 來源 |
| --- | --- |
| `Login.Success` / `Login.Failed` / `Login.Disabled` / `Login.LockedOut` | `MyUserServiceLogin`（0.9.101 起 `LockedOut` 只記造成鎖定的那一次；鎖定中的嘗試記 `Failed`、detail `reason=Locked`）|
| `User.Create` / `User.Update` / `User.Delete`（0.9.95 起為軟刪除）/ `User.Restore` / `User.Purge` / `User.Unlock`（0.9.101 起，解除登入鎖定）/ `User.Enable` / `User.Disable`（0.9.120 起，清單點狀態標籤；detail `account=…`）| `MyUserService` |
| `Team.Enable` / `Team.Disable` / `Category.Enable` / `Category.Disable` | `TeamService`、`CategoryService` 的 `SetEnabledAsync`（0.9.120 起，清單點狀態標籤；detail `name=…`）|
| `User.ProfileUpdate` | `ProfileService`（使用者在個人資料頁改自己的姓名，0.9.102 起；detail 只寫 `field=Name`，不記姓名內容）|
| `Role.Create` / `Role.Update` / `Role.Delete`（0.9.95 起為軟刪除）/ `Role.Restore` / `Role.Purge` | `RoleViewService` |
| `Job.Trigger` / `Job.Enable` / `Job.Disable` | `ScheduledJobView`（「排程作業」頁的手動操作，0.9.96 起；排程自己跑的結果記在執行紀錄）；「系統備份」頁的立即備份也由 `BackupView` 記 `Job.Trigger`。0.10.6 起由 `ScheduledJobsController`、`BackupsController`、`DataRetentionController`（立即清理，detail `job=…; source=data-retention; expected=N`，N 由伺服器在按下時重算）寫，內容同 Server 版 |
| `AiCallLog.AutoPurge` / `TokenUsage.AutoPurge` | 排程作業的自動清除（0.9.96 起） |
| `AiAgentConversation.Delete` / `AiAgentConversation.Purge` / `AiAgentConversation.AutoPurge` | Agent 對話（0.9.143 起；0.10.22 起只有系統管理助理的對話，舊的工作流程執行紀錄 `agent=` 為流程名稱）：`AiAgentConversationService.DeleteAsync`（使用者刪自己的對話，detail `agent=…`）、「資料保留與清理」的自訂清除、排程作業「Agent 對話清理」（detail `rows=N; days=D; trigger=T`）；不記標題與內容，改標題不寫稽核 |
| `Backup.Create` / `Backup.AutoPurge` / `Backup.Download` / `Backup.Delete` | 系統備份（0.9.99 起）：`SystemBackupJob`（建立、依份數刪除）、下載（0.10.6 起 `DownloadsController` 在兌換一次性票證時寫一筆；之前是 `BackupController`，續傳只記第一段）、刪除（0.10.6 起 `BackupsController`，之前是 `BackupView`） |
| `Announcement.Create` / `Announcement.Update` / `Announcement.Delete` | `AnnouncementView`（「公告管理」頁，0.9.100 起；0.10.6 起由 `AnnouncementsController` 寫，刪除的標題取自資料庫） |
| `Notification.AutoPurge` | 排程作業「站內通知清理」（0.9.100 起） |
| `EmailLog.AutoPurge` | 排程作業「寄信紀錄清理」（0.9.167 起；detail `rows=N; days=D; trigger=T`）|
| `EmailLog.Purge` | 「資料保留與清理」頁的自訂清除寄信紀錄（0.9.167 起） |
| `Notification.Test` | `NotificationTestService`（「系統健康監控」的發送測試通知，0.9.122 起；detail `recipients=N`，發送失敗加 `; failed`） |
| `SystemParameter.Update` / `SystemParameter.Reset` | `SystemParameterView`（「系統參數」頁的修改與還原，0.9.98 起；0.10.6 起由 `SystemParametersController` 寫；Detail `key=…; old=…(來源); new=…(來源)`） |
| `Project.AutoPurge` / `Category.AutoPurge` / `Team.AutoPurge` / `User.AutoPurge` / `Role.AutoPurge` | 排程作業「已刪除資料清理」（0.9.97 起）：每種資料每次一筆彙總，無操作者，`TargetId` 為 `*`，Detail 為 `rows=N; days=D; trigger=T; items=#Id 名稱, …`（上限 1000 字） |
| `Permission.Denied` | `HasPermissionAttribute`（API 動作級授權被拒）；0.9.78 起 Blazor 頁面權限拒絕也記（0.10.8 以前 Server 頁經 `AuthenticationStateHelper.RecordPageAccessDeniedAsync`，0.10.9 起已刪除）；0.10.2 起 WASM 頁面由開頁紀錄 `POST /api/v1/shell/page-view` 依選單的權限鍵判斷後記（Detail `via=WASM`）|
| `Project.FileDownload` | `DownloadsController`（0.10.3 起兌換下載票證時；之前是 `ProjectFileController`） |
| `LogLevel.Apply` / `LogLevel.Restore` | `LogLevelSettingView`（0.10.5 起由 `LogLevelController` 的 `apply`／`restore` 寫）|
| `LogViewer.AiAnalyze` / `LogViewer.AiAnalyzeExportPdf` | `LogViewerView`（0.10.5 起由 `LogsController` 的 `ai`／`ai/pdf` 寫；使用者中斷串流時也寫）|
| `Audit.Purge` / `Audit.ClearAll` | `AuditLogView`（0.9.42 起；0.10.4 起由 `AuditLogsController` 的 `purge`／`clear-all` 寫）|
| `Audit.Verify` | 完整性驗證（0.9.175 起）：稽核紀錄頁的「驗證完整性」（0.10.4 起由 `AuditLogsController.Verify` 寫）與排程作業「稽核紀錄完整性檢查」；`TargetType=AuditLog`、`TargetId=*`，結果有問題時 `Success=false`；Detail `result=ok\|failed; signed=N; issues=M; legacy=L; keyMismatch=K; trigger=T`（排程為 `Schedule`、補跑為 `CatchUp`、在排程作業頁按「立即執行」為 `Manual`、稽核紀錄頁的按鈕為 `manual`）。寫在檢查之後，所以這一筆本身由下一次檢查驗證 |
| `Email.Test` | `EmailTestService`（系統健康監控頁的寄信測試，0.9.59 起；detail 只有 provider 與成敗，不含收件者）|
| `Ai.QuotaBlocked` | `AiAccountingChatClient`（AI 用量上限在送出前擋下，0.9.109 起；detail `operation=…; limit=…; used=…`）|
| `Ai.ToolInvoked` / `Ai.ToolDenied` / `Ai.ToolRejected` | Agent 工具（0.9.142 起）：`AiAgentToolFactory`（執行成功或失敗、權限擋下）、`AiToolApprovals`（核准時權限擋下，detail 加 `stage=approval`）、`AiAgentRunner`（使用者拒絕核准）；detail `agent=…; tool=…; permission=…`，不記參數與回傳內容 |
| `Prompt.Update` / `Prompt.Activate` | `PromptTemplateView`（「AI 提示詞」頁，0.9.108 起；0.10.5 起由 `PromptTemplatesController` 寫）與系統管理助理的 `AiAssistantTools`；detail 記 `version=…`（經系統管理助理時另加 `via=`），不記提示詞內容 |
| `AdminPatrol.AutoPurge` | 排程作業「巡檢報告清理」（0.9.161 起；detail `rows=N; days=D; trigger=T`，不記報告內容）|

**系統管理助理與定時巡檢**：系統管理助理（0.9.155 起）執行的查詢與異動沿用原頁面的動作代碼（例如 `User.Search`、`Prompt.Activate`），detail 加 `via=admin-assistant`（0.9.155～0.9.156 的舊紀錄是 `source=`），工具本身另記 `Ai.ToolInvoked` 等。定時巡檢（0.9.161 起）沒有登入使用者，工具稽核的操作者留空（清單顯示「（系統／匿名）」），detail 最後註明「排程巡檢」。

## 七、已知限制與規劃中需求

**已知限制**

1. ~~動作代碼沒有單一來源~~ —— 0.9.78 已收斂到 `AuditActions` 並以測試守門。
   下拉選單仍讀資料庫 DISTINCT：舊資料可能有已更名或移除的代碼。
2. ~~`AuditLog.OccurredAt` 沒有單欄索引~~ —— 0.9.166 起有 `IX_AuditLog_OccurredAt`（migration `AddAuditLogOccurredAtIndex`），
   本頁的時間條件、保留期限清理與登入失敗告警的時間窗計數都用得到。
3. 時區以伺服器為準（見 §四）。
4. ~~清除與清空沒有保留期限的自動化~~ —— 0.9.78 起每日自動清理，0.9.96 起改由排程作業執行（§五）。

**已實作（0.9.166）**

- `OccurredAt` 索引 migration（見上方第 2 點）。
- 登入失敗告警：寫完 `Login.Failed`、`Login.LockedOut`、`Login.TwoFactorFailed`、`Login.Sso.Failed` 的稽核後，
  `AuditLogService` 呼叫 `LoginFailureAlertService` 統計全系統最近 `LoginFailureAlertSettings:WindowMinutes`（預設 10）分鐘內的失敗，
  達 `Threshold`（預設 20，`0`＝不告警）次就通知全體管理員並寄信（分類 `LoginFailureBurst`），內文列出最常被試的帳號與 IP，
  `CooldownMinutes`（預設 60）內不重發。原規劃的「同一帳號短時間大量失敗」由帳號鎖定涵蓋（鎖定時已通知管理員），
  所以告警改抓不分帳號、不分 IP 的橫向猜測。

**0.9.171 新增**

- `User.DuplicateAccountRenamed`：升級到 0.9.171 時，migration 發現不分大小寫重複的帳號，保留管理員（其次 Id 最小的），其他改名為「原帳號 (重複-Id)」並停用，每筆寫一列（由 migration 直接寫入，沒有操作者）。

**0.9.173 新增**

- 本頁工具列的「目標類型」「目標識別（精確）」（§3.1）與五個資料頁的「異動歷程」（§3.4）。
- 索引 `IX_AuditLog_TargetType_TargetId_OccurredAt`（migration `AddAuditLogTargetIndex`）：精確查詢與異動歷程都以 (`TargetType`, `TargetId`) 等值比對、依 `OccurredAt` 排序。
- 系統管理助理的 `search_audit_logs` 多了 `targetType`、`targetId` 參數（精確比對；有給目標、沒給起始時間時預設往前查 366 天；有給目標、沒指定動作時排除瀏覽類），見 [系統管理助理 PRD](系統管理助理-prd.md)。
- 已知限制：異動歷程只看得到保留期限（`LogRetentionSettings:AuditLogDays`，預設 365 天）內的紀錄；0.9.78 之前分類、團隊、專案的異動沒有稽核，看不到。`*.Export` 的目標不是單一筆資料，不會出現在異動歷程。

**0.9.174 新增**

- 記錄範圍開關 `AuditLogSettings:RecordFieldChanges`／`RecordBrowsing`（預設都關，§六）與瀏覽類保留天數 `LogRetentionSettings:AuditBrowsingLogDays`（預設 90，§五）；三者都在「系統參數」。
- ⚠️ 升級影響：(1) 升級後立刻停止記錄修改前後差異與瀏覽類事件，需要時到「系統參數」打開；(2) 升級後第一次排程（03:00）會刪掉超過 90 天的舊瀏覽類紀錄；(3) 瀏覽開關關閉時，誰看過稽核紀錄、異動歷程的打開與點選、系統管理助理的查詢稽核都不會留下紀錄。
- 已知限制：開關打開前的瀏覽與差異不會補記；瀏覽類與異動類靠動作代碼區分，常數取錯名字會被歸錯類（`AuditConventionTests` 守門）。

**0.9.175 新增**

- 防竄改簽章與完整性驗證（§九）：`AuditLog` 新增 `Chain`、`Seq`、`PrevHash`、`Hash`、`ContentHash`、`KeyId` 六欄，新資料表 `AuditChainHead`（migration `20261108101222_AddAuditLogHashChain`）；新路徑 `SystemSettings:ExternalFileSystem:AuditKeyPath`；新排程作業「稽核紀錄完整性檢查」；新動作代碼 `Audit.Verify`。
- ⚠️ 升級影響：(1) 正式環境的 appsettings 要設定 `AuditKeyPath`，而且必須是獨立目錄（啟動驗證會擋重疊）；(2) 金鑰刻意不進系統備份，要另外離線保存一份；(3) 升級前的紀錄沒有簽章，顯示「升級前的紀錄，未納入完整性驗證」，不會被驗證；(4) 每筆稽核寫入都要排隊（§九「代價」）。
- 已知限制見 §9.6。

**規劃中需求**（不屬於目前驗收範圍）

- 無。

## 八、測試

- `AuditLogServiceTests`、`ClientIpProviderTests`（0.9.117 起）：來源 IP 寫入、IPv4-mapped 轉換、無情境時為 null（`SetCircuitClientIp` 0.10.22 起已移除）。
- `AuditLogQueryServiceTests`（28 項）：時區三條、過濾（關鍵字含來源 IP、來源 IP 前綴篩選）、排序、同秒分頁不重複、
  總筆數、下拉去重排序、清除門檻以 UTC 計算、清空。
- `AdminOnlyPermissionTests`：`角色_稽核紀錄` 不得出現在角色矩陣。
- `MenuPermissionConsistencyTests`：`AuditLogView.razor.cs` 列於 `AdminOnlyViews`，
  不得使用 `CheckAccessPage`。
- `MenuIconTests`：`fact_check` 已列入允許的 classic Material Icons。
- `DataAccessServiceLifetimeTests`：`AuditLogQueryService` 與 `AuditLogService`（0.9.131 起）必須注入 `IDbContextFactory`。
- 0.9.131 起：`AuditDiffTests`（比對、排除、遮罩、集合差異）、`AuditContentFileStoreTests`（拒絕根目錄外的路徑）、
  `AuditLogServiceTests`（內容檔、Debug 同步不含內容）、`AuditLogQueryServiceTests`（清除與清空連同內容檔）、
  `ViewAuditTests`（查詢摘要不含搜尋原文）、`CategoryServiceTests`／`MyUserServiceAssignmentTests`／`CategoryTeamRepositoryUniquenessTests`（差異內容；後者 0.10.2 隨 Repository 刪除）。
- 0.10.2 起：`CategoryTeamApiTests` 的 `AuditHistory_ForAdmins_ShouldListTheChanges_AndAuditOpeningAndViewingOnce`、`AuditHistory_ForNonAdmins_ShouldReturn403`（異動歷程 API）與 `OpeningAWasmPage_WithoutPermission_ShouldAuditTheDenial_LikeTheServerPage`；分類、團隊 API 每個動作只寫一筆稽核（`*_FullLifecycle_ShouldWork_AndWriteEachAuditOnce`）。
- `PageAuthorizationTests`：`/audit-logs` 自動納入受保護路由掃描。
- 0.10.4 起：`AuditEmailLogApiTests` 9 項（稽核搜尋的篩選與畫面相同且只有使用者按查詢時才寫稽核、本地時間不帶時差且 UTC 以 Z 結尾、每頁筆數不合法回 400、選項列出動作／目標類型／保留天數、明細回傳該列與內容並只寫一筆 `Audit.View`、匯出含 BOM 的 CSV 並寫 `Audit.Export`、清除／清空／驗證與畫面相同並記錄自己、寄信紀錄依狀態與關鍵字篩選且新的在前不寫稽核、非管理員每個端點都 403 且拒絕有稽核）；
  `ApiAuthorizationConventionTests`（每個 Web API action 都要標註 `[HasPermission]`／`[AdminOnly]`／`[SignedInUser]`／`[AllowAnonymous]`，除了 `[AllowAnonymous]` 都要有 `[Authorize]`）；`ExportTests` 改用 `AuditLogExport.CsvColumns`（欄位與舊版一致）。
- 0.9.173 起：`AuditLogQueryServiceTests` 多 7 項（目標精確比對、只給目標類型或只給目標識別、目標類型清單去重排序、異動歷程只列異動且新的在前、異動歷程分頁、模型有目標索引、`NotBrowsing_ShouldAgreeWithIsBrowsing_ForEveryActionCode`）；
  `AuditConventionTests.IsBrowsing_ShouldBeTrueExactlyForSearchAndViewConstants`（只有常數名稱叫 Search／View 的才是瀏覽類）；
  `Components/AuditHistoryTests` 5 項（按鈕只給管理員、只列這一筆的異動並在點選時讀內容與寫 `Audit.View`、空狀態、非管理員不載入）；
  `AuditHistoryConventionTests` 6 項（五個資料頁與 CRUD 樣板都有兩顆按鈕、一個對話窗、正確的目標類型）；
  `AccountsAssistantToolsTests.SearchAuditLogs_ByTarget_IsExact_AndLooksBackAYearByDefault`、`SearchAuditLogs_UnknownTargetType_ShouldWarnAndNotStoreTheText`。
- 0.9.174 起：`AuditLogServiceTests` 多 8 項（瀏覽開關關閉時 `Page.View`／`Category.Search`／`Audit.View` 什麼都不寫、開啟時寫列與內容檔、差異開關關閉時保留列丟掉差異、沒注入設定時等同預設、每次寫入重讀開關、讀設定丟例外時異動照寫）；
  `AuditConventionTests.CarriesFieldDiff_ShouldBeTrueExactlyForUpdateConstants`；
  `AuditLogQueryServiceTests` 多 2 項（只清瀏覽類時保留更舊的異動列與差異檔、只清異動類時不動瀏覽列）；
  `RetentionJobTests` 多 3 項（兩類各用自己的天數、瀏覽類天數 0 時保留瀏覽列、月份目錄只依較舊的門檻收）。
- 0.9.175 起（防竄改，§九）：
  - `AuditHashV1Tests` 8 項：簽章格式 V1 的固定答案（另以獨立的 Python 實作交叉驗算過）；**格式一改就紅**，要改格式只能另加 V2。
  - `AuditChainStoreTests` 9 項：序號連續、鏈頭跟著前進、清除只刪最舊一段並推進起點、清空後接著編號、`BEGIN IMMEDIATE` 寫鎖，以及兩個寫入端各 50 筆在不同連線上同時寫入不缺號、不重號。
  - `AuditIntegrityVerifierTests` 16 項：每一種竄改都抓得到（改內容、刪中間、刪最新、塞入未簽章、偽造金鑰代號、改內容檔、鏈頭被改／被刪、金鑰更換、資料庫倒回），正常清除與清空全部之後不誤報。
  - `AuditIntegrityConventionTests` 4 項：只有 `AuditChainStore` 可以新增、修改、刪除 `AuditLog` 與 `AuditChainHead`；`AuditKeyPath` 與其他資料目錄重疊、在網站目錄底下或沒填時啟動驗證失敗，獨立目錄時通過。
  - 既有測試同步：`SystemBackupTests`（備份不含金鑰）、`ScheduledJobWiringTests`（13 個排程作業）、`OptionsValidationTests`、`RetentionJobTests`、`ApiIntegrationTests.CreateSettings`（隔離 `AuditKeyPath`）。

## 九、防竄改與完整性驗證（0.9.175 起）

### 9.1 要解決的問題與威脅模型

稽核紀錄是責任證據，但它就放在 SQLite 檔裡：拿得到資料庫檔的人（主機管理員、拿到備份的人、入侵者）可以直接改內容、刪掉幾筆、或塞入假紀錄，畫面上完全看不出來。
0.9.175 起每筆新紀錄都以 HMAC-SHA256 簽章並串成雜湊鏈，金鑰放在資料庫以外，讓「只動了資料庫」的竄改一定會被發現。

| 對手 | 能不能發現 |
| --- | --- |
| 只拿到資料庫檔（或一份備份）的人：改內容、刪紀錄、塞紀錄、把資料庫換回舊版 | 能（§9.4） |
| 只改了內容檔（修改差異、查詢條件）的人 | 能：內容檔的 SHA-256 在簽章裡 |
| **同時拿到資料庫與金鑰檔**的人 | ⚠️ 不能：可以改完再重算簽章（§9.6 限制 1） |

### 9.2 做法

- **兩條鏈**：異動鏈（`Chain=0`，異動與安全事件）與瀏覽鏈（`Chain=1`，`Page.View`、`*.Search`、`*.View`，依 `AuditActions.IsBrowsing`）。兩類保留天數不同（365／90，§五），放同一條鏈的話刪掉中間的瀏覽紀錄會讓鏈斷掉；分開後清除只從每條鏈**最舊的一端**刪，正常清除不會看起來像竄改。
- **每筆紀錄**：`Seq`（鏈內序號，從 1 連續遞增）、`PrevHash`（同一條鏈上一筆的 `Hash`，第一筆為 64 個 0）、`ContentHash`（內容檔位元組的 SHA-256，沒有內容檔為 null）、`KeyId`（金鑰代號）、`Hash`（涵蓋全部欄位、`ContentHash` 與 `PrevHash` 的 HMAC-SHA256，不含 `Id`）。簽章格式為 V1（`AuditHashV1`），**一上線就不能改**，要改只能另加 V2。
- **鏈頭** `AuditChainHead`：每條鏈一列，記起點（`StartSeq`、`StartPrevHash`，清除時往前推）、終點（`LastSeq`、`LastHash`，下一筆從這裡接）、`KeyId`、`KeySinceSeq`（從這個序號起由目前金鑰簽章）、`LegacyMaxId`（開始保護時升級前紀錄的最大 `Id`）、`CreatedAt`、`RebuiltAt`，整列以 `Mac` 簽章。
- **寫入**：只經 `AuditChainStore`。每次新增、清除、清空都在同一個 `BEGIN IMMEDIATE` 交易裡「讀鏈頭 → 改資料 → 更新鏈頭並重新簽章」，外面再加一個靜態 semaphore（等超過 30 秒就讓這一筆稽核失敗並記 Error，不卡住業務操作），所以多人同時寫也不會缺號或重號。
  - **不把竄改洗成正常**：鏈頭簽章不對、金鑰代號被改（而金鑰其實沒換）、或終點被改小時，鏈頭標記為不可信任；之後的寫入照常進行（終點被改小時從實際最後一筆接續），但不替它重新簽章，直到「清空全部」。
  - **金鑰真的換了才接受**：鏈頭的金鑰代號和目前不同時，只有「本行程剛產生新金鑰」或「資料庫以外的檢查點記的是另一把金鑰」才算換了金鑰，兩條鏈的鏈頭一起改用新金鑰。
- **清除**：每批 5000 筆一個交易，**刪之前先驗**這一段（從鏈頭起點連續、前後筆接得上、目前金鑰簽的都驗得過）；驗不過或鏈頭不可信任時，這條鏈停在那裡不刪並記 Error —— 把紀錄的時間改舊，不能讓清除替攻擊者刪掉證據。
- **金鑰**：第一次寫稽核時自動產生在 `SystemSettings:ExternalFileSystem:AuditKeyPath`（預設 `C:\temp\MyProject\AuditKey`）的 `audit-hmac.key`（32 個隨機位元組，base64）。同一個資料夾還有每天的檢查寫下的檢查點 `audit-chain-checkpoint.json`（資料庫以外的「上次看到鏈到哪裡」）。
  - ⚠️ **金鑰刻意不進系統備份**（`SystemBackupService` 不打包）：金鑰和資料庫在同一份備份裡，拿到備份的人就能改紀錄再重算簽章。請另外離線保存一份（見 [系統備份與還原 PRD](系統備份與還原-prd.md)、備份與還原操作手冊）。
  - 啟動驗證（`SystemSettingsValidator`）擋下：沒填或不是絕對路徑；與任何其他資料目錄（`DatabasePath`、`DownloadPath`、`UploadPath`、`ProjectFilePath`、`ExceptionPath`、`TokenUsagePath`、`AiCallLogPath`、`AiAgentConversationPath`、`AuditContentPath`、`DataProtectionKeyPath`）或 `BackupPath` 重疊（相同、在其下或包含它）；在網站目錄底下。
  - 是 `ExternalFileSystem` 的第 12 個目錄；系統健康監控頁會檢查它（名稱 `AuditKey`）。
- **驗證**：`AuditIntegrityVerifier` 在唯讀快照交易（DEFERRED）裡逐批重算，不會擋住同時發生的稽核寫入；內容檔在快照結束後才檢查。

### 9.3 畫面

- 工具列「驗證完整性（檢查有沒有被改、被刪）」（`verified_user`，驗證中停用）→ 對話窗「稽核紀錄完整性驗證」（尺寸共用 `.audit-log-modal`，純唯讀、Esc 關閉）：
  - 綠色「全部正確」或紅色「發現問題」標籤，加一句摘要，例如「檢查 12 筆，全部正確（5 筆升級前的紀錄未納入）。」、「檢查 N 筆，發現 M 個問題（…；K 筆是別的金鑰簽的、無法驗證內容）。」
  - 有問題時列出「問題清單」，最多 50 個（超過時標題寫「列出前 50 個，共 M 個」）。
  - 下方說明：這裡只比對、不更新檢查點；每天 03:30 的排程「稽核紀錄完整性檢查」發現問題時會通知全體管理員並寄信；「金鑰不同」是金鑰遺失或換主機沒帶金鑰造成的，只能確認序號連續、無法驗證內容，不算問題。
  - 每按一次寫一筆 `Audit.Verify`（`trigger=manual`）。
  - 0.10.4 起經 `POST api/v1/audit-logs/verify`；WASM 端這個請求最長等 30 分鐘，不受一般 100 秒逾時（§3.5）。
- 明細窗的「簽章」欄（§3.3）。
- 「清空全部」的確認文字（§五）。

### 9.4 抓得到的竄改

| 情況 | 問題代碼 | 訊息重點 |
| --- | --- | --- |
| 內容被改 | `BadHash` | 「異動鏈 #6（編號 12）：內容和簽章不符（被改過）。」 |
| 中間被刪 | `Gap` | 「缺少 #7（被刪除）」 |
| 最新幾筆被刪 | `TailMissing` | 「缺少最後的 #8～#9，共 2 筆（被刪除）」；之後再寫新紀錄仍持續回報 |
| 最舊幾筆被偷刪（不是經由清除） | `Gap` | 鏈頭的起點沒有跟著推進，所以從起點到第一筆之間「缺少 #1～#2」 |
| 前一筆被換掉 | `BrokenLink` | 「和前一筆接不上」 |
| 鏈頭被改（含只改金鑰代號） | `HeadTampered` | 「鏈頭的簽章或金鑰代號不對」；之後的寫入**不會**替它重新簽章，直到「清空全部」 |
| 鏈頭的終點被改小 | `HeadTampered` | 之後的寫入從實際最後一筆接續（稽核不會停擺），並標記鏈頭被改過 |
| 把紀錄的時間改舊，想讓排程清除替他刪掉 | `BadHash` | 清除前先驗簽章，驗不過就不刪；每日檢查回報「內容和簽章不符」 |
| 移除唯一索引後塞入重複序號 | `DuplicateSeq` | 「序號重複」 |
| 檢查點檔被改壞 | `CheckpointUnreadable` | 這一次無法比對「資料庫有沒有被倒回」，排程會重寫一份 |
| 鏈頭被刪 | `HeadRebuilt` | 下一次寫入時依現有紀錄自動重建，回報一次「鏈頭曾經不見…已重建」 |
| 繞過系統塞入未簽章的紀錄 | `Unsigned` | 「編號 37：沒有簽章」—— 沒有簽章、`Id` 又大於 `LegacyMaxId` |
| 偽造金鑰代號 | `KeyIdForged` | `KeySinceSeq` 之後的紀錄 `KeyId` 不是目前金鑰 |
| 內容檔被改／不見 | `ContentModified`／`ContentMissing` | 「內容檔被改過」／「內容檔不見了」 |
| 金鑰檔不見 | `KeyFileRestored` | 用執行中的金鑰自動寫回並回報 |
| 金鑰檔被換 | `KeyFileReplaced` | 網站下次重新啟動就會改用新金鑰 |
| 金鑰變更 | `KeyChanged` | 和上次檢查時不同（金鑰遺失後重新產生，或被更換） |
| 資料庫被倒回舊版、或最新紀錄連同鏈頭被換回舊的 | `RolledBack` | 靠檢查點：目前的終點比上次檢查時舊，或同一個序號的雜湊不同 |
| 整條鏈連同鏈頭被刪 | `HeadMissing` | 靠檢查點：上次檢查時還在 |

**一次性與持續性**：金鑰變更、鏈頭重建、資料庫倒回這類一次性事件，只由第一次看到它的每日檢查回報一次（之後檢查點的基準跟著移動）；被改、缺號、接不上、未簽章、內容檔不符這類持續性的損壞，每天都會回報，直到「清空全部」為止。

**升級前的紀錄**：0.9.175 前寫入的紀錄沒有簽章，歸類為「升級前的紀錄，未納入完整性驗證」，另外計數、不算問題。但開始保護之後出現沒有簽章、`Id` 又比 `LegacyMaxId` 大的紀錄，一律回報（`Unsigned`）。

**金鑰遺失或換主機沒帶金鑰**：第一次寫稽核時自動產生新金鑰，寫入照常；之前的紀錄歸類為「金鑰不同」（警告，另外計數、不算問題：仍檢查序號連續，但無法驗證內容）。每日檢查對「金鑰變更」告警一次。

### 9.5 每日檢查

- 排程作業 `AuditIntegrityVerify`「稽核紀錄完整性檢查」，預設 `30 3 * * *`（避開 02:00 的系統備份與 03:00 的各項清理）；說明「重算每一筆稽核紀錄的簽章，檢查有沒有被改、被刪或繞過系統塞入；發現問題時作業結果為失敗，並通知全體管理員（含電子郵件）。」
- 發現問題時作業回傳失敗，由排程框架既有的「作業失敗」通知送給全體管理員並寄信（不另設通知分類，避免收到兩則）；失敗訊息列出前 3 個問題，並請管理員到本頁按「驗證完整性」看完整清單。
- 每次執行都更新檢查點，並寫一筆 `Audit.Verify`。問題另以 ERROR 寫進 NLog 日誌檔 —— 日誌在資料庫以外，就算有人刪了通知也還在。
- 詳見 [排程作業 PRD](排程作業-prd.md)。

### 9.6 已知限制與代價

1. ⚠️ **同時拿到資料庫與金鑰檔的人無法被發現**：可以改完再重算簽章。所以金鑰資料夾的 NTFS 權限只給應用程式集區帳號與 Administrators，並且不進備份。
2. 檢查點一天只更新一次：「先存一份舊的鏈頭，之後刪掉最新幾筆再把舊鏈頭放回去」，最晚在下一次每日檢查才會發現。
3. 金鑰遺失時，之前的紀錄只能顯示「金鑰不同」，無法驗證內容（請另外離線保存金鑰）。
4. 用備份正常還原資料庫，也會觸發一次「資料庫被倒回」告警 —— 這是預期中的，備份與還原操作手冊已註明。
5. 持續性的問題（被改、缺號）每天都會告警，目前只能等那幾筆過了保留天數被清掉，或「清空全部」才會停；沒有「調查完畢、保留紀錄、重新起算」的管理操作。
6. 每日檢查可以在排程作業頁停用，能改資料庫的人也能把它停掉；停掉之後不會再有告警（排程作業頁與系統健康監控看得到它停用或逾期）。
7. 升級前的紀錄還沒清光之前，指定一個比 `LegacyMaxId` 小、目前沒被用到的 `Id` 塞入的未簽章紀錄，會被當成升級前的紀錄（清空全部或升級前的紀錄清光後 `LegacyMaxId` 歸零，就沒有這個問題）。
8. **代價：每筆稽核寫入都要排隊**（靜態 semaphore＋SQLite `BEGIN IMMEDIATE`），而且要更新鏈頭；打開「記錄瀏覽類事件」時每次換頁都會拿一次寫鎖。以 20～100 人的內部系統評估可以接受。

### 9.7 維運重點

- `AuditKeyPath` 資料夾的 NTFS 權限只給應用程式集區帳號與 Administrators。
- `audit-hmac.key` 另外離線保存一份，和系統備份分開放。
- 換主機時把金鑰檔複製到新主機相同的 `AuditKeyPath`，否則舊紀錄只能顯示「金鑰不同」。
- 從備份還原後會出現一次「資料庫被倒回」告警，屬預期。

> 返回 [PRD 主控台](README.md)
