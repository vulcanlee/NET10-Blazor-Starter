# 系統巡檢報告 PRD

- 文件版本：1.0
- 文件狀態：部分實作（里程碑 M1 已實作；M2～M4 見第九章「規劃中需求」）
- 現行系統版本：0.10.39
- 首次實作版本：0.10.39
- 最後核對日期：2026/11/20

## 一、目標與範圍

### 背景

系統狀況分散在系統健康監控、日誌檢視、系統例外紀錄、排程作業、系統備份、資料保留與清理、寄信紀錄、Token 用量、稽核紀錄等十幾頁，管理員每天逐頁看太花時間。
0.9.161 起的「系統管理助理巡檢」（排程作業 `AdminPatrol`）由 AI 呼叫工具、自己判讀狀態，有三個問題：燈號由模型決定，同樣的狀況不一定得到同樣的結果；每次都要花錢，沒有設定 AI 的衍生專案完全沒有巡檢；通知內文含模型產生的文字（速查表 §6.7 的明文例外）。

### 目標

使用者 2026/11/20 提出：系統定時產生巡檢報告（日報、週報、月報），以 PDF 分析報告寄給管理員；報告最前面是全面摘要，讓人一眼看出「已經壞了什麼、快要壞什麼、只要注意什麼、哪顆磁碟要準備擴充」；每一項問題都寫出「不處理會怎樣、怎麼處理、處理完會怎樣」。

| 包含 | 不包含 |
|---|---|
| 新頁面 `/system-reports`「系統巡檢報告」：清單、篩選、報告內容、立即產生、舊版巡檢報告 | 自動修正問題（只列出建議與「前往處理」的頁面） |
| 燈號由程式規則判定（紅／橙／黃／綠），AI 只寫說明、不能改燈號 | 讓 AI 判定燈號，或讓 AI 文字進入通知與 Email 內文 |
| 每天的容量快照與成長預估（幾天後用完、建議擴充量） | 即時監控與告警（仍由各頁面與既有通知負責） |
| 日報（M1）；PDF 與 Email 附件（M2）；週報、月報與季度清查（M3）；系統管理助理指定區間（M4） | CPU／記憶體趨勢、憑證到期等（列在候選需求） |
| 取代 0.9.161 的「系統管理助理巡檢」排程 | 系統管理助理在對話中「幫我巡一下系統」的 AI 巡檢（M1 不變，M4 改用同一套規則） |

### 成功標準

1. 每天上班打開最新一份日報，看燈號就知道有沒有需要介入的事；沒有紅燈或橙燈就不必逐頁檢查。
2. 同樣的系統狀況，每次產生的燈號與數字都一樣；沒有設定 AI 時報告照常產生。
3. 磁碟用完之前，依預估日期與建議擴充量提早準備，不會等到寫入失敗才發現。

## 二、使用者與入口

| 頁面 | 路由 | 選單 | 權限 |
|---|---|---|---|
| 系統巡檢報告 | `/system-reports`（`?report={報告編號}` 打開指定報告；`?legacyReport={編號}` 打開舊版巡檢報告）| 系統管理 › 系統設定與維運 › 系統巡檢報告（`Menu.json` id=70，圖示 `assessment`）| **只限管理員**（WASM 頁 `Permissions.IsAdmin`、API `[AdminOnly]`；權限鍵不放進角色矩陣，`AdminOnlyPermissionTests` 守門）|

- 通知鈴鐺與 Email 的連結是 `/system-reports?report={報告編號}`。
- 系統管理助理頁的「巡檢報告」按鈕改為前往本頁；舊連結 `/admin-assistant?patrolReport={編號}` 轉到 `/system-reports?legacyReport={編號}`。
- 操作說明：`MyProject.Web/Datas/Help/system-reports.md`（已登記 `HelpTopics.json`）。

## 三、使用者決定（2026/11/20）

| 項目 | 決定 |
|---|---|
| 報告種類 | 一種報告、三種頻率（日報／週報／月報），各自在「排程作業」頁啟用或停用 |
| 設定位置 | 產生時間、容量門檻、AI 說明開關在「系統參數」頁的「系統巡檢報告」分類；保留天數在「資料保留」分類 |
| 涵蓋區間 | 日曆區間：日報＝前一天 00:00～24:00；週報＝上週；月報＝上個月 |
| 燈號 | 由程式規則判定（同樣狀況同樣結果）；AI 只依結果寫整體狀況、處理順序與下期預測 |
| 容量預估 | 新增每天一次的容量快照，依成長速度預估幾天後用完，並提出擴充建議 |
| 門檻 | 容量的紅燈、橙燈、黃燈門檻可在系統參數調整；其他規則的門檻先固定（候選需求：每條規則個別門檻） |
| 收件人 | 所有啟用中的管理員 |
| 何時寄出 | 週報、月報一律寄出；日報只有整份為橙燈以上才通知與寄信 |
| Email 內容 | 程式產生的摘要＋PDF 附件（M2）＋報告連結；**信件內文不放 AI 文字** |
| PDF | 去除敏感內容（不放路徑、設定值、例外內文、帳號），不加密碼；PDF 內含 AI 說明（M2） |
| 各頻率的段落 | 日報看昨天發生的事；週報、月報另有趨勢、備份可還原驗證、AI 費用（M3） |
| 帳號與權限清查 | 每季一次，附在 1／4／7／10 月的月報（M3） |
| 頁面 | 新增獨立頁面，不放在系統管理助理頁 |
| 舊巡檢 | 0.9.161 的「系統管理助理巡檢」排程由日報取代；舊報告改在本頁「舊版巡檢報告」查看，依保留天數自然過期 |
| 系統管理助理 | 對話中可指定區間產生報告（M4）；這是唯讀的產生動作，**不經核准窗**（明文例外，寫在系統管理助理 PRD） |
| 開發方式 | 分四個里程碑，見第八章 |

## 四、燈號與規則

### 4.1 燈號

| 燈號 | 意思 | 通知 |
|---|---|---|
| 紅燈 | 已發生問題，要馬上處理 | 日報通知全體管理員 |
| 橙燈 | 即將發生，有期限要處理（發現附期限日期） | 日報通知全體管理員 |
| 黃燈 | 注意即可，不急 | 只存報告 |
| 綠燈 | 正常，沒有需要處理的事項 | 只存報告 |

- 整份報告的燈號是所有發現裡最嚴重的那一個；畫面以文字＋AntDesign 標籤顏色顯示，不用表情符號。
- 每一項發現有：規則代碼、區塊、燈號、標題、現況、不處理會怎樣、怎麼處理、處理完會怎樣、前往處理的頁面、期限（橙燈必有）。
- 規則寫在 `MyProject.Web/SystemReports/SystemReportRules.cs`（純函式）；文字只放固定用語與數字，不放路徑、設定值、例外訊息、帳號。

### 4.2 規則目錄（M1）

區間天數記為 D（日報 D＝1）；「每天」的門檻乘上 D。

| 區塊 | 規則代碼 | 條件 | 燈號 | 前往 |
|---|---|---|---|---|
| 系統健康 | `HLT-*` | 不花錢模式的健康檢查（略過 LLM API 探測與 SMTP 登入）：資料庫、檔案系統、身分驗證、快取、日誌、日誌管線、網站、API 異常 | 異常紅燈、注意黃燈 | 對應頁面 |
| 系統健康 | `HLT-SECURITY`、`HLT-AIPRICING` | 安全設定、AI 計費表不是綠燈 | 黃燈 | `/system-health` |
| 錯誤與例外 | `ERR-LOG` | Error 以上的日誌 ≥ 100×D 筆；≥ 1 筆 | 紅燈；黃燈 | `/logs` |
| 錯誤與例外 | `ERR-EXC-NEW` | 區間內第一次出現的例外種類；其中有已發生 20 次以上、或來自背景作業、系統啟動 | 黃燈；紅燈 | `/system-exceptions` |
| 錯誤與例外 | `ERR-CLIENT` | 還在發生的瀏覽器端錯誤 ≥ 5 種 | 黃燈 | `/system-exceptions` |
| 排程作業 | `JOB-MASTER` | 排程總開關關閉 | 紅燈 | `/scheduled-jobs` |
| 排程作業 | `JOB-SETTINGS` | 排程設定驗證失敗 | 黃燈 | `/scheduled-jobs` |
| 排程作業 | `JOB-FAILED` | 啟用中的作業最後一次失敗（稽核紀錄完整性檢查另列） | 紅燈 | `/scheduled-jobs` |
| 排程作業 | `JOB-RECOVERED` | 區間內失敗過、之後已成功 | 黃燈 | `/scheduled-jobs` |
| 排程作業 | `JOB-OVERDUE` | 超過預定時間一小時以上沒有執行 | 紅燈 | `/scheduled-jobs` |
| 排程作業 | `JOB-DISABLED` | 作業被停用（系統巡檢報告各作業與系統備份除外） | 黃燈 | `/scheduled-jobs` |
| 排程作業 | `JOB-INTERRUPTED` | 最後一次執行被中斷 | 黃燈 | `/scheduled-jobs` |
| 備份 | `BKP-NONE` | 沒有任何備份 | 紅燈 | `/backups` |
| 備份 | `BKP-AGE` | 最新備份比 1 個備份週期＋2 小時舊；比 2 個週期＋2 小時舊 | 黃燈；紅燈 | `/backups` |
| 備份 | `BKP-DISABLED` | 「系統備份」排程停用（期限＝最新備份＋1 個週期） | 橙燈 | `/scheduled-jobs` |
| 備份 | `BKP-SPACE` | 備份磁碟放不下下一份備份（最新備份大小＋資料庫 1.1 倍；期限＝下一次備份） | 橙燈 | `/backups` |
| 備份 | `BKP-SAMEVOL` | 備份與資料庫在同一顆磁碟 | 黃燈 | `/backups` |
| 容量 | `CAP-VOLUME` | 每顆磁碟：可用低於紅燈百分比或紅燈 GB → 紅燈；低於橙燈百分比 → 橙燈；預估 ≤ 紅／橙／黃燈天數用完 → 紅／橙／黃燈（取較嚴重者，期限＝預估用完日） | 紅／橙／黃 | `/data-retention` |
| 容量 | `CAP-WAL` | 資料庫 -wal 檔 > max(256 MB, 主檔的 50%) | 黃燈 | `/database-usage` |
| 容量 | `CAP-FREELIST` | 可回收空間 > 500 MB 且 > 主檔的 30% | 黃燈 | `/database-usage` |
| 寄信 | `MAIL-OFF` | 寄信方式是 None | 黃燈 | `/system-health` |
| 寄信 | `MAIL-STUCK` | 有信等待寄出超過 2 小時 | 紅燈 | `/email-logs` |
| 寄信 | `MAIL-FAILED` | 寄送失敗 ≥ 3 封或超過 20%；否則 ≥ 1 封 | 紅燈；黃燈 | `/email-logs` |
| AI（有設定 AI 才判斷） | `AI-FAIL` | 失敗率 ≥ 50% 且呼叫 ≥ 5 次；失敗率 ≥ 10% | 紅燈；黃燈 | `/ai-call-logs` |
| AI | `AI-QUOTA` | 全系統每月上限已達到；照目前速度月底前會達到（期限＝預估達到日）；已用 ≥ 80% | 紅燈；橙燈；黃燈 | `/system-parameters`、`/token-usage` |
| AI | `AI-BLOCKED` | 有呼叫因用量上限被擋（`Ai.QuotaBlocked`） | 黃燈 | `/token-usage` |
| AI | `AI-UNPRICED` | 有成功的呼叫沒有費用（模型沒有價格） | 黃燈 | `/token-usage` |
| 帳號與安全 | `SEC-REUSE` | `Token.ReuseDetected` | 紅燈 | `/audit-logs` |
| 帳號與安全 | `SEC-INTEGRITY` | 稽核紀錄完整性檢查最後一次失敗 | 紅燈 | `/audit-logs` |
| 帳號與安全 | `SEC-BURST` | 發出過「登入失敗次數異常」告警 | 紅燈 | `/audit-logs` |
| 帳號與安全 | `SEC-LOGINFAIL` | 登入失敗（含兩步驟驗證與 Google 登入）≥ 20×D 次 | 黃燈 | `/audit-logs` |
| 帳號與安全 | `SEC-LOCKED` | 有帳號正被鎖定 | 黃燈 | `/myusers` |
| 帳號與安全 | `SEC-ADMIN2FA` | 有啟用中的管理員沒有啟用兩步驟驗證 | 黃燈 | `/system-parameters` |
| 帳號與安全 | `SEC-DENIED` | `Permission.Denied` ≥ 20×D 次 | 黃燈 | `/audit-logs` |
| 任何區塊 | `SYS-SOURCE` | 產生報告時讀不到某個來源 | 黃燈「無法查詢：…」 | `/system-exceptions` |

### 4.3 容量預估

- 「容量快照」作業（`CapacitySnapshot`，預設每天 05:00，排在備份與清理之後）記下各磁碟、資料庫檔與資料目錄的用量到資料表 `CapacitySnapshot`（一天一個代碼一列，重記會覆蓋）；產生報告時當天還沒有快照會順便存一份。
- 對「已用空間」做最小平方法直線回歸（不用可用空間，磁碟擴充後才不會打亂斜率），只用最近 90 天；**少於 7 天不預估**，畫面顯示「資料累積中」，只看剩餘百分比。
- 擴充建議：預估 365 天內用完的磁碟，建議量＝一年的成長量＋15%。
- 資料目錄量測逾時時記「部分」，數字是下限（畫面顯示「至少 …」）。

## 五、報告內容與產生流程

### 5.1 報告內容

| 區塊 | 內容 |
|---|---|
| 全面摘要 | 整體燈號與一句話結論；已發生問題（附處理方式）、即將發生（附期限）、注意即可（最多 10 項）、需要準備擴充容量（附預估用完日與建議擴充量）、下期預測 |
| AI 說明 | `## 整體狀況`、`## 建議處理順序`、`## 下期預測` 三段；燈號與數字以摘要為準 |
| 每一項發現 | 燈號、區塊、標題、現況、不處理會怎樣、怎麼處理（附「前往處理」連結）、處理完會怎樣、期限 |
| 磁碟容量 | 每顆磁碟的用途、總容量、可用空間、每天成長、預估用完日期 |
| 資料目錄 | 資料庫與各資料目錄的大小與檔案數 |
| 統計 | 錯誤日誌、例外種類、排程失敗、寄信、AI 呼叫與費用、登入失敗、權限被拒、鎖定中的帳號、最新備份 |
| 量測限制 | 數字的限制，例如日誌管線的計數在網站重新啟動後歸零 |

### 5.2 產生流程

`SystemReportGenerator`：收集事實（`SystemReportCollector`，每個來源各自 try/catch，讀不到變成一則黃燈）→ 補今天的容量快照 → 規則判燈號（`SystemReportRules`）→ 組摘要（`SystemReportSummarizer`）→ AI 說明（`SystemReportNarrator`）→ 存檔（`SystemReportService`）。產生本身不通知、不寄信，由呼叫端依觸發方式決定（`SystemReportDelivery`）。

| 觸發 | 區間 | 通知 |
|---|---|---|
| 排程「系統巡檢日報」（`SystemReportDaily`）、補跑 | 前一天 00:00～24:00；同一天已有排程產生的報告就略過 | 整份橙燈以上：全體啟用中的管理員（站內＋Email）；否則不通知 |
| 「排程作業」頁的立即執行 | 同上（不略過） | 同上 |
| 本頁「立即產生」 | 指定日期（今天以前、保留天數內） | 勾「產生後寄給所有管理員」：不論燈號都通知全體管理員；不勾：只以站內通知告訴產生者 |

- 「立即產生」不在 HTTP 請求裡產生（可能超過一分鐘），而是帶參數（`Arguments`，JSON：日期、是否寄送）放進排程佇列，由排程器在背景執行；已經在佇列或執行中回 409「已經有一份報告正在產生，請等它完成。」。稽核 `Job.Trigger`，detail `job=SystemReportDaily; date=…; send=…; via=system-reports`。
- 通知與 Email 內文**只用程式產生的文字**（結論、紅燈與橙燈的標題與期限、黃燈數量，純文字、1000 字內）與報告連結，不放 AI 說明；通知分類 `SystemReport`，以 `SourceKey`＝`SystemReport:{報告編號}` 去重。
- 需要通知卻沒有通知到人時作業回失敗（「…；通知發送失敗，報告已存在「系統巡檢報告」頁。」），由排程框架另發排程失敗通知。

### 5.3 AI 說明

- 提示詞「系統巡檢報告」（鍵 `SystemReport`，「AI 提示詞」頁第 9 個）；模型用 `AiSettings` 的預設模型。
- 輸入只有去識別的發現（規則代碼、燈號、標題、數字、期限、處理頁面），不含帳號、路徑、例外內文；超過長度時先捨棄黃燈。指示不放現在時間（提示詞快取）。
- 不呼叫 AI 的情況：日報全部綠燈、系統參數「AI 寫報告說明」關閉、沒有設定 AI（狀態 `Skipped`）。呼叫失敗、逾時或格式不對時狀態 `Fallback`，報告照常產生與通知。
- Token 用量作業名稱「系統巡檢報告（AI 說明）」，ConversationId＝報告編號；沒有登入使用者，只受全系統用量上限。舊紀錄的「系統管理助理（排程巡檢）」照舊顯示。

## 六、排程、設定與資料

### 6.1 排程作業

| 名稱 | 顯示名稱 | 時間 | 說明 |
|---|---|---|---|
| `SystemReportDaily` | 系統巡檢日報（0.10.39 新增）| 系統參數「日報產生時間」（預設 07:30），經 `IScheduledJobCronOverride` 提供；**不可**用 `ScheduledJobSettings:Jobs` 覆寫（驗證器擋下） | 見 5.2 |
| `CapacitySnapshot` | 容量快照（0.10.39 新增）| `0 5 * * *`（可在設定檔覆寫） | 見 4.3 |
| `AdminPatrolReportRetention` | 巡檢報告與容量快照清理（原「巡檢報告清理」，作業名稱不變）| `0 3 * * *` | 刪除超過保留天數的報告（含舊版巡檢報告）與內容檔、過期的容量快照；稽核 `SystemReport.AutoPurge`（detail `reports=N; days=D; snapshots=S; snapshotDays=SD; trigger=T`）與舊版 `AdminPatrol.AutoPurge` |
| `AdminPatrol` | 系統管理助理巡檢（**0.10.39 移除**）| — | 列入 `ScheduledJobSettingsValidator.RetiredJobNames`：衍生專案設定檔還留著它的覆寫時略過不驗，不會讓網站無法啟動 |

### 6.2 系統參數

| 分類 | 參數 | 鍵 | 預設 | 範圍 |
|---|---|---|---|---|
| 系統巡檢報告 | 日報產生時間 | `SystemReportSettings:DailyTime` | 07:30 | HH:mm |
| 系統巡檢報告 | AI 寫報告說明 | `SystemReportSettings:AiNarrativeEnabled` | 開 | 開／關 |
| 系統巡檢報告 | 容量紅燈：可用空間低於（%） | `SystemReportSettings:CapacityRedFreePercent` | 5 | 1～50 |
| 系統巡檢報告 | 容量紅燈：可用空間低於（GB） | `SystemReportSettings:CapacityRedFreeGb` | 2 | 0～100000（0＝不檢查） |
| 系統巡檢報告 | 容量紅燈：預估幾天內用完 | `SystemReportSettings:CapacityRedForecastDays` | 7 | 1～3650 |
| 系統巡檢報告 | 容量橙燈：可用空間低於（%） | `SystemReportSettings:CapacityOrangeFreePercent` | 15 | 1～90 |
| 系統巡檢報告 | 容量橙燈：預估幾天內用完 | `SystemReportSettings:CapacityOrangeForecastDays` | 30 | 1～3650 |
| 系統巡檢報告 | 容量黃燈：預估幾天內用完 | `SystemReportSettings:CapacityYellowForecastDays` | 90 | 1～3650 |
| 資料保留 | 巡檢報告保留天數（日報） | `SystemReportSettings:DailyRetentionDays` | 90 | 1～3650（舊版巡檢報告也用它；取代 `AdminPatrolSettings:RetentionDays`） |
| 資料保留 | 容量快照保留天數 | `SystemReportSettings:CapacitySnapshotRetentionDays` | 730 | 30～3650 |

- 驗證：紅燈百分比 < 橙燈百分比；預估天數「紅 < 橙 < 黃」。設定檔區段 `SystemReportSettings` 取代 `AdminPatrolSettings`。
- 日報的開關是「排程作業」頁的啟用開關，不在系統參數。

### 6.3 資料

| 資料 | 說明 |
|---|---|
| 資料表 `SystemReport` | 清單、篩選、通知與清理用的中繼資料：報告編號、頻率（Daily／Weekly／Monthly／Custom）、區間、產出時間、燈號、紅橙黃數量、觸發方式、產生者帳號、排程執行紀錄、AI 說明狀態與模型、通知狀態、內容檔 |
| 資料表 `CapacitySnapshot` | 快照日期、量測時間、種類（Volume／Folder／Database）、代碼、已用、總容量、可用、檔案數、狀態 |
| 內容檔 | `{AiAgentConversationPath}/AdminPatrol/{yyyyMM}/{報告編號}.json`（與舊版 `.md` 同一個目錄，經 `AdminPatrolReportFileStore`）；備份只在「備份 Agent 對話」開啟時包含 |
| Migration | `20261120054158_AddSystemReportAndCapacitySnapshot` |
| 資料保留總覽 | 「巡檢報告」列＝系統巡檢報告＋舊版巡檢報告，前往 `/system-reports`；新增「容量快照」列（沒有檔案、不提供自訂清除，前往 `/system-reports`） |

### 6.4 Web API（`api/v1/system-reports`，`[AdminOnly]`）

| 方法 | 路徑 | 說明 |
|---|---|---|
| POST | `search` | 依種類、燈號、產生日期分頁查詢 |
| GET | `{id}` | 單份報告的內容 |
| POST | `generate` | 立即產生（背景）；今天以後或早於保留天數 400，已在產生中 409 |
| GET | `legacy` | 舊版巡檢報告清單 |
| GET | `legacy/{id}` | 單份舊版巡檢報告 |

0.10.39 起移除 `api/v1/admin-assistant/patrol-reports` 與 `patrol-reports/{id}`。

## 七、技術決定

- **燈號只由規則決定**：`SystemReportRules` 是純函式，輸入是收集到的事實與設定，輸出是發現；新增會影響系統健康的功能時加一條規則並補門檻邊界測試。AI 不能改燈號與數字。
- **不花錢的健康檢查**：`ISystemHealthService.GetPassiveItemsAsync` 略過 LLM API 探測（會計費）與寄信伺服器登入，其餘 12 項照常；AI 與寄信的狀況改由實際的呼叫與寄信紀錄判斷。健康項目的 `Evidence` 含路徑與設定值，**不可以**複製進報告。
- **收集不寫稽核**：這是系統自己的巡檢，不是有人在瀏覽資料，不呼叫會寫瀏覽稽核的查詢方法（例如系統管理助理的工具）。
- **程式提供的執行時間**：新增 `IScheduledJobCronOverride`（singleton、便宜、不丟例外、不碰資料庫），`SystemReportScheduleProvider` 把「07:30」換成「30 7 * * *」；排程器每分鐘重讀，改了從現在重新排（不追溯），管理頁顯示的時間也用它。被接手的作業不可以再用 `ScheduledJobSettings:Jobs` 覆寫。
- **手動觸發帶參數**：`ScheduledJobOverviewService.TryTrigger` 可帶 `Arguments`（JSON），經 `ScheduledJobTriggerQueue` 傳到 `ScheduledJobContext.Arguments`；長時間的產生一律在排程器裡跑，不在 HTTP 請求裡。
- **移除作業不讓舊設定擋啟動**：`ScheduledJobSettingsValidator.RetiredJobNames = ["AdminPatrol"]`。
- **內容檔沿用舊目錄**：放在 Agent 對話目錄的 `AdminPatrol` 子目錄（JSON），備份規則跟著 `IncludeAiAgentConversations`，不另開設定；資料表不放 AI 說明（速查表 §6.7）。
- **AI 文字不進通知**：通知與 Email 內文只用程式產生的文字，取代 0.9.161 的 §6.7 明文例外（舊例外隨巡檢移除）。報告內容不放路徑、設定值、例外內文與帳號（只放數量），因為報告之後會以 PDF 寄出系統外。
- 規則見速查表 §6.34。

## 八、里程碑

| 里程碑 | 內容 | 狀態 |
|---|---|---|
| M1 | 新頁面、規則引擎與日報、容量快照與預估、AI 說明、通知、系統參數、資料保留、取代「系統管理助理巡檢」、舊版巡檢報告移到本頁 | 0.10.39 已實作 |
| M2 | PDF 報告（含 AI 說明，去除敏感內容、不加密碼）與 Email 附件 | 待實作 |
| M3 | 週報與月報（各自在排程作業頁開關）、備份可還原驗證、AI 費用段落、每季帳號與權限清查（附在 1／4／7／10 月的月報） | 待實作 |
| M4 | 系統管理助理：對話中指定區間產生報告與 PDF（「幫我巡一下系統」改用同一套規則；唯讀、不經核准窗） | 待實作 |

## 九、規劃中需求

以下內容**尚未實作**，不屬於目前的驗收範圍。

- **M2**：PDF 版面與 Email 附件；週報、月報一律寄出。
- **M3**：週報（上週）、月報（上個月）的產生時間在系統參數設定、開關在排程作業頁；趨勢比較、備份可還原驗證、AI 費用；每季帳號與權限清查。
- **M4**：系統管理助理以規則引擎產生指定區間的報告（種類「指定區間」），取代對話中的 AI 巡檢。
- **候選需求**（尚未決定是否做）：清查的簽核流程、AI 模型 Deprecated 檢查、憑證到期、CPU／記憶體趨勢、每條規則個別調整門檻、在頁面上執行 VACUUM／WAL 維護。

## 十、驗收與測試

- 守門：`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`SystemParameterTests`（每個參數鍵都要寫進日誌與設定檔說明）、`PageHelpCatalogTests`、`UserManualCoverageTests`。
- 功能測試：`SystemReportRulesTests`（每條規則的邊界、門檻來自系統參數、健康只看狀態不複製 Evidence、讀不到的來源變黃燈、通知不含 AI 說明、AI 輸入去識別）、`SystemReportSchedulingTests`（容量預估、日報時間轉 cron、程式提供的 cron 優先、驗證器擋覆寫並忽略已移除的作業）、`SystemReportServiceTests`（內容檔、查詢、快照、清理作業）、`SystemReportsApiTests`（非管理員 403、立即產生的驗證與稽核、報告不含路徑、舊版報告端點）。
- 品質關卡結果見 [changelog](../changelog/2026-11-20-系統巡檢報告M1.md)（品質關卡結果待補）。

## 十一、相關程式與文件

- `src/MyProject/MyProject.Web/SystemReports/`（`SystemReportRules`、`SystemReportCollector`、`SystemReportGenerator`、`SystemReportSummarizer`、`SystemReportNarrator`、`SystemReportDelivery`、`SystemReportScheduleProvider`、`CapacityForecast`、`CapacityProbe`、`VolumeResolver`）
- `src/MyProject/MyProject.Web/Scheduling/Jobs/SystemReportDailyJob.cs`、`CapacitySnapshotJob.cs`、`AdminPatrolReportRetentionJob.cs`、`Scheduling/IScheduledJobCronOverride.cs`
- `src/MyProject/MyProject.Web/Controllers/SystemReportsController.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/SystemReportService.cs`、`SystemReportMetricsService.cs`、`CapacitySnapshotService.cs`
- `src/MyProject/MyProject.AccessDatas/Models/SystemReport.cs`、`CapacitySnapshot.cs`；`MyProject.Models/Systems/SystemReportSettings.cs`、`SystemReportModels.cs`；`MyProject.Dtos/Admins/SystemReportDtos.cs`
- `src/MyProject/MyProject.Web.Client/Pages/SystemReportPage.razor`、`Views/Admins/SystemReportView.*`、`SystemReportDetailModal.*`、`AdminPatrolReportsModal.*`（舊版巡檢報告）、`Api/SystemReportApi.cs`
- 相關 PRD：[排程作業](排程作業-prd.md)、[系統參數](系統參數-prd.md)、[資料保留與清理](資料保留與清理-prd.md)、[系統管理助理](系統管理助理-prd.md)、[系統健康監控](系統健康監控-prd.md)、[站內通知與公告](站內通知與公告-prd.md)、[AI 提示詞管理](AI提示詞管理-prd.md)、[Token 用量](Token用量-prd.md)
- 設定：[日誌與設定檔說明](../operations/日誌與設定檔說明.md)；規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §6.34
