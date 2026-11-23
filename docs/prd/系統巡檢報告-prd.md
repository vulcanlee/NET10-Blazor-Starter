# 系統巡檢報告 PRD

- 文件版本：1.3
- 文件狀態：已實作（里程碑 M1～M4 已實作；候選需求見第九章）
- 現行系統版本：0.10.42
- 首次實作版本：0.10.39
- 最後核對日期：2026/11/23

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
| 取代 0.9.161 的「系統管理助理巡檢」排程 | 系統管理助理在對話中「幫我巡一下系統」由 AI 自己判讀的巡檢（M1～M3 不變；M4〔0.10.42〕起改用同一套規則，見 5.7） |

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
- 0.10.42 起（M4）系統管理助理在對話中巡檢時產生一份「指定區間」報告，回覆下方的「查看報告」前往 `/system-reports?report={報告編號}`、「下載 PDF」直接下載（見 5.7）。
- 操作說明：`MyProject.Web/Datas/Help/system-reports.md`（已登記 `HelpTopics.json`）。

## 三、使用者決定（2026/11/20）

| 項目 | 決定 |
|---|---|
| 報告種類 | 一種報告、三種頻率（日報／週報／月報），各自在「排程作業」頁啟用或停用 |
| 設定位置 | 產生時間、容量門檻、AI 說明開關在「系統參數」頁的「系統巡檢報告」分類；保留天數在「資料保留」分類 |
| 涵蓋區間 | 日曆區間：日報＝前一天 00:00～24:00；週報＝上週；月報＝上個月；指定區間（M4）＝系統管理助理對話中指定的起迄日（沒講日期＝昨天 00:00 到現在） |
| 燈號 | 由程式規則判定（同樣狀況同樣結果）；AI 只依結果寫整體狀況、處理順序與下期預測 |
| 容量預估 | 新增每天一次的容量快照，依成長速度預估幾天後用完，並提出擴充建議 |
| 門檻 | 容量的紅燈、橙燈、黃燈門檻可在系統參數調整；其他規則的門檻先固定（候選需求：每條規則個別門檻） |
| 收件人 | 所有啟用中的管理員 |
| 何時寄出 | 週報、月報一律寄出；日報只有整份為橙燈以上才通知與寄信 |
| Email 內容 | 程式產生的摘要＋PDF 附件（M2，0.10.40 起）＋報告連結；**信件內文不放 AI 文字** |
| PDF | 去除敏感內容（不放路徑、設定值、例外內文、帳號），不加密碼；PDF 內含 AI 說明（M2，0.10.40 起，見 5.4）。帳號的唯一例外見下方「報告裡的帳號」 |
| 各頻率的段落 | 日報看昨天發生的事；週報、月報另有趨勢、容量成長與規劃、備份可還原驗證，月報再加 AI 費用（M3，0.10.41 起，見 5.5） |
| 帳號與權限清查 | 每季一次，附在 1／4／7／10 月產生的月報（M3）。實作的解讀：區間是 3／6／9／12 月的那份月報（在 1／4／7／10 月產生），清查涵蓋**剛結束的那一季**（例 10 月初的月報清查 7～9 月）；只列清單，不做確認或簽核流程（PDF 可留存作為清查紀錄） |
| 報告裡的帳號（M3，2026/11/20 決定） | 報告頁與 PDF **只在**月報的「AI 費用」（費用最高的 10 位使用者）與「帳號與權限清查」兩節的表格列出帳號與姓名——清查要列出帳號才有意義，PDF 是資安稽核的留存。各節的重點（Highlights）、站內通知、Email 內文與送給 AI 的資料**仍只有數量** |
| 保留天數 | 日報、週報、月報各自設定（M3）：預設日報 90 天、週報 365 天、月報 1095 天（季度清查的留存） |
| 頁面 | 新增獨立頁面，不放在系統管理助理頁 |
| 舊巡檢 | 0.9.161 的「系統管理助理巡檢」排程由日報取代；舊報告改在本頁「舊版巡檢報告」查看，依保留天數自然過期 |
| 系統管理助理 | 對話中可指定區間產生報告（M4，0.10.42 起，見 5.7）；只存一份內部報告、不寄信、不改業務資料，**不經核准窗**（明文例外，寫在系統管理助理 PRD 與速查表 §6.7「Agent 工具的三條紅線」第 3 點）；在對話請求裡直接產生，是速查表 §6.34 第 7 點「產生報告不在 HTTP 請求裡跑」的明文例外 |
| 開發方式 | 分四個里程碑，見第八章 |

## 四、燈號與規則

### 4.1 燈號

| 燈號 | 意思 | 通知 |
|---|---|---|
| 紅燈 | 已發生問題，要馬上處理 | 日報通知全體管理員 |
| 橙燈 | 即將發生，有期限要處理（發現附期限日期） | 日報通知全體管理員 |
| 黃燈 | 注意即可，不急 | 日報只存報告 |
| 綠燈 | 正常，沒有需要處理的事項 | 日報只存報告 |

週報、月報不論燈號一律通知全體管理員並寄信（0.10.41 起）。

- 整份報告的燈號是所有發現裡最嚴重的那一個；畫面以文字＋AntDesign 標籤顏色顯示，不用表情符號。
- 每一項發現有：規則代碼、區塊、燈號、標題、現況、不處理會怎樣、怎麼處理、處理完會怎樣、前往處理的頁面、期限（橙燈必有）。
- 規則寫在 `MyProject.Web/SystemReports/SystemReportRules.cs`（純函式）；文字只放固定用語與數字，不放路徑、設定值、例外訊息、帳號。

### 4.2 規則目錄（M1；`BKP-VERIFY` 為 M3）

區間天數記為 D（日報 D＝1、週報 D＝7、月報 D＝那個月的天數、指定區間 D＝起迄日的天數）；「每天」的門檻乘上 D。

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
| 備份 | `BKP-VERIFY` | 週報、月報的備份可還原驗證（0.10.41 起，見 5.6）：Failed「最新的備份無法還原」；Unverifiable「這次沒有完成備份可還原驗證」（空間不夠、備份檔剛好被清掉或讀取錯誤） | 紅燈；黃燈 | `/backups` |
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
| 全面摘要 | 整體燈號與一句話結論；已發生問題（附處理方式）、即將發生（附期限）、注意即可（最多 10 項）、需要準備擴充容量（附預估用完日與建議擴充量）、本期重點（週報、月報各節的 Highlights，0.10.41 起）、下期預測 |
| AI 說明 | `## 整體狀況`、`## 建議處理順序`、`## 下期預測` 三段；燈號與數字以摘要為準 |
| 每一項發現 | 燈號、區塊、標題、現況、不處理會怎樣、怎麼處理（附「前往處理」連結）、處理完會怎樣、期限 |
| 週報、月報的章節（0.10.41 起） | 趨勢比較、容量成長與規劃、備份可還原驗證、AI 費用（月報）、帳號與權限清查（1／4／7／10 月產生的月報），見 5.5 |
| 磁碟容量 | 每顆磁碟的用途、總容量、可用空間、每天成長、預估用完日期 |
| 資料目錄 | 資料庫與各資料目錄的大小與檔案數 |
| 統計 | 錯誤日誌、例外種類、排程失敗、寄信、AI 呼叫與費用、登入失敗、權限被拒、鎖定中的帳號、最新備份 |
| 量測限制 | 數字的限制，例如日誌管線的計數在網站重新啟動後歸零 |

### 5.2 產生流程

`SystemReportGenerator`：收集事實（`SystemReportCollector`，每個來源各自 try/catch，讀不到變成一則黃燈）→ 補今天的容量快照 → 規則判燈號（`SystemReportRules`）→ 組摘要（`SystemReportSummarizer`）→ AI 說明（`SystemReportNarrator`）→ 存檔（`SystemReportService`）。產生本身不通知、不寄信，由呼叫端依觸發方式決定（`SystemReportDelivery`）。

| 觸發 | 區間 | 通知 |
|---|---|---|
| 排程「系統巡檢日報」（`SystemReportDaily`）、補跑 | 前一天 00:00～24:00；同一天已有排程產生的報告就略過 | 整份橙燈以上：全體啟用中的管理員（站內＋Email）；否則不通知 |
| 排程「系統巡檢週報」（`SystemReportWeekly`，0.10.41 起）、補跑 | 上週一 00:00～下週一 00:00（排程時段所在日往前 7 天所在的週）；同一週已有排程產生的報告就略過 | 一律通知全體啟用中的管理員（站內＋Email 附 PDF） |
| 排程「系統巡檢月報」（`SystemReportMonthly`，0.10.41 起）、補跑 | 上個月 1 日 00:00～這個月 1 日 00:00；同一個月已有排程產生的報告就略過 | 一律通知全體啟用中的管理員（站內＋Email 附 PDF） |
| 「排程作業」頁的立即執行 | 同上（不略過） | 同上 |
| 本頁「立即產生」 | 指定日期所在的區間（`SystemReportPeriod.Containing`：日報＝那一天；週報＝那一天所在的週一～週日；月報＝那一天所在的月份）；整個區間要已經過完，區間起日在那一種報告的保留天數內 | 勾「產生後寄給所有管理員」：不論燈號都通知全體管理員；不勾：只以站內通知告訴產生者 |
| 系統管理助理對話中巡檢（M4，0.10.42 起；種類 `Custom`「指定區間」、觸發 `Assistant`） | 對話中指定的起日 00:00～迄日隔天 00:00（`SystemReportPeriod.Custom`）；沒講日期＝昨天 00:00 到現在；最多 31 天、迄日不晚於今天、起日在日報的保留天數內（見 5.7） | 不通知、不寄信、不寫 AI 說明（對話的回覆就是說明）；對話回覆下方出現「查看報告」「下載 PDF」 |

- 區間的計算在 `SystemReportPeriod`（`MyProject.Web/SystemReports/SystemReportFacts.cs`）：`Containing`（某一天所在的區間）、`LastComplete`（今天以前最近一個完整的區間）、`Previous`（上一期，趨勢比較用）、`Quarter`（帳號清查涵蓋的季度＝這個月往前共三個月）、`IsPeriodic`（週報、月報）、`IncludesAiCost`（月報）、`IncludesAccountReview`（月報且區間月份是 3／6／9／12）。排程與補跑用「排程時段」的日期推算區間，補跑晚了才跑仍產生正確的區間。
- 「立即產生」不在 HTTP 請求裡產生（可能超過一分鐘），而是帶參數（`Arguments`，JSON：日期、是否寄送）放進對應作業（`SystemReportJob.JobNameFor`：日報／週報／月報）的排程佇列，由排程器在背景執行；同一個作業已經在佇列或執行中回 409「已經有一份{日報／週報／月報}正在產生，請等它完成。」。稽核 `Job.Trigger`，detail `job={SystemReportDaily／SystemReportWeekly／SystemReportMonthly}; date=…; send=…; via=system-reports`。唯一的例外是系統管理助理的巡檢（0.10.42 起，見 5.7）：在對話的串流請求裡直接產生。
- 通知與 Email 內文**只用程式產生的文字**（結論、紅燈與橙燈的標題與期限、黃燈數量；週報、月報另加每節一行「【節標題】重點」，純文字、1000 字內）與報告連結，不放 AI 說明、不放帳號；通知分類 `SystemReport`，以 `SourceKey`＝`SystemReport:{報告編號}` 去重。
- 需要通知卻沒有通知到人時作業回失敗（「…；通知發送失敗，報告已存在「系統巡檢報告」頁。」），由排程框架另發排程失敗通知。
- 0.10.40 起寄給全體管理員的信附上報告 PDF（見 5.4），信件種類 `SystemReport`；「立即產生」沒勾寄送時只發站內通知給產生者，不寄信也不產生 PDF。

### 5.3 AI 說明

- 提示詞「系統巡檢報告」（鍵 `SystemReport`，「AI 提示詞」頁第 9 個）；模型用 `AiSettings` 的預設模型。
- 輸入只有去識別的發現（規則代碼、燈號、標題、數字、期限、處理頁面），不含帳號、路徑、例外內文；超過長度時先捨棄黃燈。指示不放現在時間（提示詞快取）。
- 0.10.41 起週報、月報另送 `sections`：每節只送標題與重點（Highlights，只有數字），**不送表格**（AI 費用與帳號清查表格裡的帳號與姓名不送）。預設指示（`SystemReportNarrator.DefaultInstructions`）加一段：可以在整體狀況與下期預測引用這些重點（和上一期比較、一年後的容量、備份能不能還原）。已在「AI 提示詞」頁改過指示的，要自己補上這段才會引用。
- 只有日報全部綠燈時才略過 AI；週報、月報全部綠燈仍會請 AI 寫說明。
- 不呼叫 AI 的情況：日報全部綠燈、系統參數「AI 寫報告說明」關閉、沒有設定 AI、系統管理助理產生的指定區間報告（0.10.42 起，`SystemReportRequest.SkipNarrativeNote`；「AI 說明」一節寫「由系統管理助理在對話中產生，說明請見該次對話。」）（狀態 `Skipped`）。呼叫失敗、逾時或格式不對時狀態 `Fallback`，報告照常產生與通知。
- Token 用量作業名稱「系統巡檢報告（AI 說明）」，ConversationId＝報告編號；沒有登入使用者，只受全系統用量上限。舊紀錄的「系統管理助理（排程巡檢）」照舊顯示。

### 5.4 PDF 報告與信件附件（M2，0.10.40 起）

- `SystemReportPdfBuilder`（`MyProject.Web/SystemReports`）由存好的報告內容 JSON **隨需產生** PDF：不另存 PDF 檔、不加欄位、沒有 migration；舊版巡檢報告沒有 PDF。字型用 `AiMarkdownPdfRenderer` 內嵌的 Noto Sans TC，中文可在任意字元間換行。
- 版面順序與報告視窗相同：標題（例「系統巡檢日報 2026/11/19」）→ 系統、涵蓋區間、產生時間、整體燈號，並註明「燈號與數字由程式規則判定，AI 說明僅供參考」→ 全面摘要（燈號色塊＋結論、已發生問題與處理、即將發生與期限、注意即可最多 10 項、需要準備擴充容量、下期預測）→ AI 說明（沒有時寫出原因）→ 每一項發現（每項一個表格：現況／不處理會怎樣／怎麼處理／處理完會怎樣／期限；處理頁面寫**中文頁面名稱**，不放網址）→ 磁碟容量 → 資料目錄 → 統計 → 附錄（燈號的意思與量測限制）。
- 燈號以**文字＋淺色底色**呈現，不用表情符號。檔名只用英數字：`system-report-{頻率小寫}-{區間起日 yyyyMMdd}.pdf`（例 `system-report-daily-20261119.pdf`）。
- **下載**：報告內容視窗內右上方「下載 PDF」（下載中停用；成功提示「PDF 已產生並開始下載。」，失敗「PDF 下載失敗：…」）→ `POST api/v1/system-reports/{id}/pdf`（`[AdminOnly]`，回 `application/pdf`）。報告或內容檔不存在 404；內嵌字型缺失 500「缺少內建中文字型，請確認建置產物完整。」。報告離開系統，所以寫稽核 `SystemReport.Download`（targetType `SystemReport`、targetId 報告編號 `N` 格式、detail `format=pdf; frequency=Daily`）；看報告本身仍不寫稽核。
- **信件附件**：`SystemReportDelivery.SendToAdminsAsync` 產生 PDF，經 `NotificationRequest.Email`（`NotificationEmailOptions`：種類 `SystemReport`、附件、只加在信件內文的一句話）交給寄信層。信件內文＝原本的程式摘要＋報告連結按鈕＋「完整報告請見附件 PDF（system-report-daily-20261119.pdf）。」。
  - PDF 產生失敗：不附，內文改為「PDF 產生失敗沒有附上，請由連結到系統查看與下載報告。」並記 Error。
  - PDF 超過 `EmailAttachment.MaxTotalBytes`（8 MB）：不附，內文改為「PDF 太大沒有附上，請由連結到系統查看與下載報告。」並記 Warning。
  - 兩種情況通知照常送出，不算通知失敗。
- AI 說明只在報告視窗與 PDF，**不進**站內通知與信件內文。

### 5.5 週報與月報的章節（M3，0.10.41 起）

報告內容新增 `Sections`（`SystemReportSectionDto`：`Key`、`Title`、`Highlights`、`Tables`、`Notes`；表格 `SystemReportTableDto`：`Caption`、`Columns`、`Rows`〔排好版的文字〕、`EmptyText`），日報是空的。由 `SystemReports/SystemReportPeriodicSections.Build`（純函式）組出，代碼見 `SystemReportSectionKeys`。報告視窗與 PDF 用同一套通用的「重點＋表格」呈現，不為每一節寫專用畫面；PDF 放在「每一項發現」之後，欄寬平均分配。

| 節（`Key`） | 哪些報告 | 內容 |
|---|---|---|
| 趨勢比較（`Trend`） | 週報、月報 | 本期與上一期（`Previous()`）的新例外種類、還在發生的例外種類、排程失敗次數、寄出的信、寄送失敗的信、AI 呼叫次數與失敗、AI 費用、登入失敗、權限被拒，與變化（「+12（+20%）」「持平」；上期是 0 不算百分比）。重點列出增加最多的 3 項 |
| 容量成長與規劃（`Growth`） | 週報、月報 | 每顆磁碟的期初已用、目前已用、本期增加、總容量、一年後預估已用（依成長速度推 365 天，標示夠不夠）；資料庫與各資料目錄的期初、期末、本期增加。期初與期末取自區間內的容量快照（至少兩筆，否則「快照不足」），今天的量測也算一筆 |
| 備份可還原驗證（`BackupVerify`） | 週報、月報 | 驗證的備份（檔名、備份時間、資料庫大小、附件與資料檔數、花費時間）與每一項檢查的結果，見 5.6；沒有備份時只有一句重點 |
| AI 費用（`AiCost`） | 月報 | 本月與前一個月的費用與變化、呼叫次數、使用者數、沒有計價的呼叫；依功能、依模型（呼叫次數、費用、前一個月、變化）；費用最高的 10 位使用者（**帳號、姓名**、呼叫次數、費用；排程呼叫顯示「（系統排程）」）。資料來源 Token 用量（本地時間） |
| 帳號與權限清查（`AccountReview`） | 區間月份是 3／6／9／12 的月報（在 1／4／7／10 月產生） | 涵蓋 `Quarter`（這個月往前共三個月）。啟用與停用帳號數；管理員（**帳號、姓名**、兩步驟驗證、最後登入）；超過 90 天沒有登入（有登入紀錄看最後登入，沒有紀錄看建立時間）；沒有兩步驟驗證（Google 登入不算）；本季帳號與角色異動（建立、修改含角色與團隊、啟用停用、刪除還原與永久刪除、兩步驟驗證重設與關閉、角色的增刪改；時間、動作、操作者、對象）。每張清單最多 200 筆（超過時在說明寫總數）；最後登入取自稽核 `Login.Success`、`Login.Sso.Success`，超過稽核保留天數的查不到，顯示「沒有紀錄」。只列清單，不記錄確認結果 |

- **Highlights 只放數字與固定用語**：放在全面摘要的「本期重點」，也用在通知、信件內文（每節一行「【標題】重點」）與 AI 說明的輸入。帳號與姓名只在 AI 費用與帳號清查的**表格**裡（第三章「報告裡的帳號」）。
- 資料：上一期的統計沿用 `SystemReportMetricsService.GetAsync`；AI 費用與帳號清查是 `SystemReportMetricsService.Monthly.cs` 的 `GetAiCostAsync`、`GetAccountReviewAsync`（Business 層，不寫稽核）。每一項各自 try/catch，讀不到時變成 `SYS-SOURCE` 黃燈，趨勢、容量成長、AI 費用、帳號清查該節不出現；備份驗證那一節改寫「這次沒有驗證備份」。

### 5.6 備份可還原驗證（M3，0.10.41 起）

週報、月報產生時，`MyProject.Web/Backup/BackupVerifier`（singleton）拿**最新的一份備份**做還原腳本會做的事，證明真的還原得回來：

1. 壓縮檔打得開、`manifest.json` 讀得到、格式版本是這個版本的系統認得的。
2. 每個項目都解壓縮一遍，算出的 CRC32 要和壓縮檔記錄的相同（壓縮檔損毀會在這裡發現）；各資料夾的檔案數要和 manifest 相同。
3. 資料庫解到 `{BackupPath}/.work/verify-<guid>/`：SHA-256 要和 manifest 相同、`PRAGMA integrity_check` 是 ok、`__EFMigrationsHistory`（最新一筆與筆數）和 manifest 相同。

| 結果 | 什麼時候 | 規則 |
|---|---|---|
| `Passed` | 每一項都通過 | 不產生發現，只在章節寫「可以還原」 |
| `Failed` | 任何一項沒通過（備份有問題、還原不回來） | `BKP-VERIFY` 紅燈「最新的備份無法還原」 |
| `Unverifiable` | 這次沒辦法驗證：備份磁碟可用空間不到資料庫大小的 1.1 倍、備份檔在驗證前被清掉或搬走、讀取時 IO 錯誤 | `BKP-VERIFY` 黃燈「這次沒有完成備份可還原驗證」 |

- 不碰正在使用的資料庫、不改備份檔；暫存檔一律在 `.work/`，結束就刪（被強制結束時由下一次備份清掉）。結果只有數字與固定用語，不含路徑。
- 資料庫大時會花幾分鐘並暫時佔用約一份資料庫大小的空間，所以週報的時間預設避開系統備份（02:00），系統參數的說明也提醒避開。
- 日誌：`Backup verification finished. FileName=…, Outcome=…, Failed=…, ElapsedMs=…`（Information）；讀不到備份檔或刪不掉暫存目錄記 Warning。

### 5.7 系統管理助理指定區間（M4，0.10.42 起）

系統管理助理（`/admin-assistant`）在對話中巡檢（「幫我巡一下系統」「幫我巡一下 11/1～11/15」，分派到 `patrol`）時，改用本報告的規則引擎產生一份「指定區間」報告，取代 0.9.157 起由 AI 逐一呼叫唯讀工具、自己判讀狀態的巡檢。燈號與數字因此和本頁的報告一致。

- **工具** `run_system_report`「產生系統巡檢報告」（`Ai/Agents/AdminAssistant/ReportAssistantTools.cs`，權限 `角色_系統巡檢報告`／View，即管理員）：參數 `startDate`、`endDate` 原樣填入使用者說的日期；只在巡檢回合載入（`AdminAssistantAgent.PatrolTools`）。回給模型的只有規則的結果（整體燈號、結論、每一項發現的燈號／區塊／標題／現況／期限／處理方式／中文頁面名稱、需要擴充的磁碟、下期預測、統計），不含帳號、路徑、例外內文。
- **區間由程式解析**（`SystemReportAssistantService.ResolveRange`）：模型不知道今天幾號，所以使用者說的日期原樣傳進來，年份由程式推。
  - 兩個日期都沒給：昨天 00:00 到現在（今天到目前為止）。
  - 接受 `yyyy-MM-dd`、`yyyy/M/d`、`yyyy.M.d`、`yyyyMMdd` 與沒有年份的 `M/d`、`M-d`、`M.d`；沒有年份時取不晚於今天的最近那一年（1 月說「12/28～1/3」會跨年；「2/29」往前找到最近的閏年）。
  - 只給一個日期＝那一天；起日晚於迄日時自動對調。
  - 不產生、把說明回給模型轉述的情況：迄日晚於今天、超過 31 天（`MaxDays`；建議整月改用本頁的月報）、起日早於日報的保留天數、看不懂的日期。
- **產生**（`SystemReportAssistantService.GenerateAsync`）：種類 `Custom`、觸發 `Assistant`、產生者是目前的使用者；**不請 AI 寫說明**（`SystemReportRequest.SkipNarrativeNote`，狀態 `Skipped`，「AI 說明」一節寫「由系統管理助理在對話中產生，說明請見該次對話。」）、**不通知、不寄信**。沒有週報、月報的章節；保留天數用日報的天數。清單的「觸發方式」顯示「系統管理助理（帳號）」，種類篩選「指定區間」。
- **稽核** `SystemReport.Generate`：targetType `SystemReport`、targetId 報告編號（`N` 格式）、detail `frequency=Custom; start=yyyy-MM-dd; end=yyyy-MM-dd; via=admin-assistant`；工具外殼另照常寫 `Ai.ToolInvoked`。
- **不經核准窗**（使用者 2026/11/20 決定的明文例外）：工具會存一份報告，但只是內部報告、不寄信、不改任何業務資料，所以 `IsMutating = false`；`AdminAssistantAgentTests.PatrolTools_OnlyInPatrolTurns_AndAreTheOnlyApprovalFreeWriters` 以明確的清單守門。
- **在對話請求裡產生**（速查表 §6.34 第 7 點的明文例外）：對話的串流有心跳、使用者可以按停止；停止會取消產生，不會存出報告。
- **回覆下方的按鈕**：工具把（報告編號、標題）交給 scoped 的 `AiAgentReportOutputs`，`AiAgentChat` 在這一輪結束時（回覆失敗也一樣）在回覆後面加一則逐字稿 `AiAgentTranscriptKinds.Report`（`Text`＝報告編號 `N` 格式、`Agent`＝報告標題，例「系統巡檢指定區間 2026/11/22～2026/11/23」），隨既有的逐字稿串流送到畫面、跟著對話存檔。畫面顯示成一張卡片：標題＋「查看報告」（前往 `/system-reports?report={報告編號}`）＋「下載 PDF」（同報告視窗的下載，成功「PDF 已產生並開始下載。」、失敗「PDF 下載失敗：…」；伺服器寫 `SystemReport.Download`）。PDF 檔名 `system-report-custom-{起日 yyyyMMdd}.pdf`。
- **回覆格式**（`AdminAssistantAgent.DefaultCommonInstructions` 的巡檢段落）：一律呼叫 `run_system_report`、原樣填日期、不自己判斷燈號、不改數字、不加工具結果沒有的問題；依序用「## 總結」「## 需要處理」「## 下期預測」三段回覆（需要處理依紅、橙、黃排序，寫要做什麼、到哪一頁〔中文頁名，不寫網址〕、期限），最後提醒可按「查看報告」「下載 PDF」。追問細節時再用其他唯讀工具查。
- 已在「AI 提示詞」頁改過「系統管理助理」提示詞的，新的巡檢段落不會自動套用（已知限制）。

## 六、排程、設定與資料

### 6.1 排程作業

| 名稱 | 顯示名稱 | 時間 | 說明 |
|---|---|---|---|
| `SystemReportDaily` | 系統巡檢日報（0.10.39 新增）| 系統參數「日報產生時間」（預設 07:30），經 `IScheduledJobCronOverride` 提供；**不可**用 `ScheduledJobSettings:Jobs` 覆寫（驗證器擋下） | 見 5.2 |
| `SystemReportWeekly` | 系統巡檢週報（0.10.41 新增）| 系統參數「週報產生日（星期幾）」＋「週報產生時間」（預設星期一 08:00，`0 8 * * 1`；星期日＝cron 的 0），同樣經 `IScheduledJobCronOverride`、不可覆寫 | 上週一～週日，一律寄出；見 5.2、5.5 |
| `SystemReportMonthly` | 系統巡檢月報（0.10.41 新增）| 系統參數「月報產生日（每月幾號）」＋「月報產生時間」（預設 1 日 08:30，`30 8 1 * *`），同樣經 `IScheduledJobCronOverride`、不可覆寫 | 上個月，一律寄出；見 5.2、5.5 |
| `CapacitySnapshot` | 容量快照（0.10.39 新增）| `0 5 * * *`（可在設定檔覆寫） | 見 4.3 |
| `AdminPatrolReportRetention` | 巡檢報告與容量快照清理（原「巡檢報告清理」，作業名稱不變）| `0 3 * * *` | 刪除超過保留天數（0.10.41 起日報、週報、月報各自的天數；舊版巡檢報告用日報的天數）的報告與內容檔、過期的容量快照；稽核 `SystemReport.AutoPurge`（detail `reports=N; days=D; weeklyDays=W; monthlyDays=M; snapshots=S; snapshotDays=SD; trigger=T`，`weeklyDays`／`monthlyDays` 0.10.41 起）與舊版 `AdminPatrol.AutoPurge`；結果訊息列出三種天數 |
| `AdminPatrol` | 系統管理助理巡檢（**0.10.39 移除**）| — | 列入 `ScheduledJobSettingsValidator.RetiredJobNames`：衍生專案設定檔還留著它的覆寫時略過不驗，不會讓網站無法啟動 |

### 6.2 系統參數

| 分類 | 參數 | 鍵 | 預設 | 範圍 |
|---|---|---|---|---|
| 系統巡檢報告 | 日報產生時間 | `SystemReportSettings:DailyTime` | 07:30 | HH:mm |
| 系統巡檢報告 | 週報產生日（星期幾）（0.10.41 起） | `SystemReportSettings:WeeklyDayOfWeek` | 1 | 1～7（1＝星期一 … 7＝星期日） |
| 系統巡檢報告 | 週報產生時間（0.10.41 起） | `SystemReportSettings:WeeklyTime` | 08:00 | HH:mm |
| 系統巡檢報告 | 月報產生日（每月幾號）（0.10.41 起） | `SystemReportSettings:MonthlyDay` | 1 | 1～28（不開放 29～31，每個月都有這一天） |
| 系統巡檢報告 | 月報產生時間（0.10.41 起） | `SystemReportSettings:MonthlyTime` | 08:30 | HH:mm |
| 系統巡檢報告 | AI 寫報告說明 | `SystemReportSettings:AiNarrativeEnabled` | 開 | 開／關 |
| 系統巡檢報告 | 容量紅燈：可用空間低於（%） | `SystemReportSettings:CapacityRedFreePercent` | 5 | 1～50 |
| 系統巡檢報告 | 容量紅燈：可用空間低於（GB） | `SystemReportSettings:CapacityRedFreeGb` | 2 | 0～100000（0＝不檢查） |
| 系統巡檢報告 | 容量紅燈：預估幾天內用完 | `SystemReportSettings:CapacityRedForecastDays` | 7 | 1～3650 |
| 系統巡檢報告 | 容量橙燈：可用空間低於（%） | `SystemReportSettings:CapacityOrangeFreePercent` | 15 | 1～90 |
| 系統巡檢報告 | 容量橙燈：預估幾天內用完 | `SystemReportSettings:CapacityOrangeForecastDays` | 30 | 1～3650 |
| 系統巡檢報告 | 容量黃燈：預估幾天內用完 | `SystemReportSettings:CapacityYellowForecastDays` | 90 | 1～3650 |
| 資料保留 | 巡檢報告保留天數（日報） | `SystemReportSettings:DailyRetentionDays` | 90 | 1～3650（日報與指定區間；舊版巡檢報告也用它；取代 `AdminPatrolSettings:RetentionDays`） |
| 資料保留 | 巡檢報告保留天數（週報）（0.10.41 起） | `SystemReportSettings:WeeklyRetentionDays` | 365 | 7～3650 |
| 資料保留 | 巡檢報告保留天數（月報）（0.10.41 起） | `SystemReportSettings:MonthlyRetentionDays` | 1095 | 31～3650（月報附季度帳號清查，是資安稽核的留存，預設 3 年） |
| 資料保留 | 容量快照保留天數 | `SystemReportSettings:CapacitySnapshotRetentionDays` | 730 | 30～3650 |

- 驗證：紅燈百分比 < 橙燈百分比；預估天數「紅 < 橙 < 黃」。設定檔區段 `SystemReportSettings` 取代 `AdminPatrolSettings`。
- 日報、週報、月報的開關是「排程作業」頁的啟用開關，不在系統參數。
- 保留天數依種類取用：`SystemReportSettings.RetentionDays(frequency)`、`SystemReportService.RetentionDays(frequency)` 與 `ExpiryThreshold(frequency, now)`；清理條件與資料保留頁的「下次會清掉幾筆」共用同一個運算式（速查表 §6.29）。資料保留頁「巡檢報告」列的規則文字是「產出後保留：日報 N 天、週報 N 天、月報 N 天」。

### 6.3 資料

| 資料 | 說明 |
|---|---|
| 資料表 `SystemReport` | 清單、篩選、通知與清理用的中繼資料：報告編號、頻率（Daily／Weekly／Monthly／Custom）、區間、產出時間、燈號、紅橙黃數量、觸發方式、產生者帳號、排程執行紀錄、AI 說明狀態與模型、通知狀態、內容檔 |
| 資料表 `CapacitySnapshot` | 快照日期、量測時間、種類（Volume／Folder／Database）、代碼、已用、總容量、可用、檔案數、狀態 |
| 內容檔 | `{AiAgentConversationPath}/AdminPatrol/{yyyyMM}/{報告編號}.json`（與舊版 `.md` 同一個目錄，經 `AdminPatrolReportFileStore`）；備份只在「備份 Agent 對話」開啟時包含 |
| Migration | `20261120054158_AddSystemReportAndCapacitySnapshot`（M3 週報、月報沿用同一張表與內容檔格式，只在內容 JSON 多 `Sections`，沒有新的 migration；M4 指定區間沿用既有的頻率 `Custom` 與觸發 `Assistant`，也沒有 migration） |
| 資料保留總覽 | 「巡檢報告」列＝系統巡檢報告＋舊版巡檢報告，前往 `/system-reports`；新增「容量快照」列（沒有檔案、不提供自訂清除，前往 `/system-reports`） |

### 6.4 Web API（`api/v1/system-reports`，`[AdminOnly]`）

| 方法 | 路徑 | 說明 |
|---|---|---|
| POST | `search` | 依種類、燈號、產生日期分頁查詢；回應 `SystemReportListDto` 帶 `DailyRetentionDays`，0.10.41 起另有 `WeeklyRetentionDays`、`MonthlyRetentionDays` |
| GET | `{id}` | 單份報告的內容 |
| POST | `generate` | 立即產生（背景）；`Frequency` 是 `Daily`／`Weekly`／`Monthly`（週報、月報 0.10.41 起，其他 400「報告種類只能是日報、週報或月報。」），`Date` 是區間內任一天。區間還沒過完 400（「只能產生今天以前的日報（今天還沒過完）。」「這一週還沒過完，只能產生上週以前的週報。」「這個月還沒過完，只能產生上個月以前的月報。」）；區間起日早於那一種報告的保留天數 400「只能產生最近 N 天內的{日報／週報／月報}。」；同一個作業已在產生中 409「已經有一份{週報}正在產生，請等它完成。」 |
| POST | `{id}/pdf` | 下載 PDF（0.10.40 起，由內容隨需產生，回 `application/pdf`）；報告或內容檔不存在 404、內嵌字型缺失 500；稽核 `SystemReport.Download` |
| GET | `legacy` | 舊版巡檢報告清單 |
| GET | `legacy/{id}` | 單份舊版巡檢報告 |

0.10.39 起移除 `api/v1/admin-assistant/patrol-reports` 與 `patrol-reports/{id}`。0.10.42 起系統管理助理產生的指定區間報告不新增端點：產生走系統管理助理的對話 API（工具 `run_system_report`），查看與下載沿用上表的 `{id}` 與 `{id}/pdf`。

## 七、技術決定

- **燈號只由規則決定**：`SystemReportRules` 是純函式，輸入是收集到的事實與設定，輸出是發現；新增會影響系統健康的功能時加一條規則並補門檻邊界測試。AI 不能改燈號與數字。
- **不花錢的健康檢查**：`ISystemHealthService.GetPassiveItemsAsync` 略過 LLM API 探測（會計費）與寄信伺服器登入，其餘 12 項照常；AI 與寄信的狀況改由實際的呼叫與寄信紀錄判斷。健康項目的 `Evidence` 含路徑與設定值，**不可以**複製進報告。
- **收集不寫稽核**：這是系統自己的巡檢，不是有人在瀏覽資料，不呼叫會寫瀏覽稽核的查詢方法（例如系統管理助理的工具）。
- **程式提供的執行時間**：新增 `IScheduledJobCronOverride`（singleton、便宜、不丟例外、不碰資料庫），`SystemReportScheduleProvider` 把「07:30」換成「30 7 * * *」；排程器每分鐘重讀，改了從現在重新排（不追溯），管理頁顯示的時間也用它。被接手的作業不可以再用 `ScheduledJobSettings:Jobs` 覆寫。
- **手動觸發帶參數**：`ScheduledJobOverviewService.TryTrigger` 可帶 `Arguments`（JSON），經 `ScheduledJobTriggerQueue` 傳到 `ScheduledJobContext.Arguments`；長時間的產生一律在排程器裡跑，不在 HTTP 請求裡。
- **移除作業不讓舊設定擋啟動**：`ScheduledJobSettingsValidator.RetiredJobNames = ["AdminPatrol"]`。
- **內容檔沿用舊目錄**：放在 Agent 對話目錄的 `AdminPatrol` 子目錄（JSON），備份規則跟著 `IncludeAiAgentConversations`，不另開設定；資料表不放 AI 說明（速查表 §6.7）。
- **AI 文字不進通知**：通知與 Email 內文只用程式產生的文字，取代 0.9.161 的 §6.7 明文例外（舊例外隨巡檢移除）。報告內容不放路徑、設定值、例外內文與帳號（只放數量），因為報告之後會以 PDF 寄出系統外。
- **PDF 隨需產生、不存檔**（0.10.40）：內容 JSON 已是唯一來源，PDF 只是另一種呈現；不存檔就不必處理 PDF 與內容不一致、保留與清理、備份大小，舊報告升級後也能下載。代價是每次下載與寄信都要重新排版。
- **附件放在加密的內文 JSON 裡**（0.10.40）：寄信佇列的內文本來就以 Data Protection 加密、寄出後清除；附件跟著它走就同樣受保護、同樣清除，不新增欄位與 migration，重試時寄出相同的位元組，升級前排入的舊格式照樣解得開。附件是通用能力（`EmailMessage.Attachments`），見[寄信服務 PRD](寄信服務-prd.md) §4.3。
- **大小上限與退路**（0.10.40）：一封信附件總計 ≤ 8 MB（`EmailAttachment.MaxTotalBytes`），由呼叫端檢查；超過或產生失敗時不附，信中寫明原因並請收件人由連結下載——通知一定要送到，不能因為附件而整封失敗。
- **下載要稽核**（0.10.40）：看報告不寫稽核（報告全體管理員共用），但下載的 PDF 會離開系統，所以寫 `SystemReport.Download`。
- **三種報告共用一個作業類別**（0.10.41）：抽象 `SystemReportJob`＋`SystemReportDailyJob`／`SystemReportWeeklyJob`／`SystemReportMonthlyJob`，只差在 `Frequency`；區間一律由 `SystemReportPeriod` 推算，「要不要寄」由 `SystemReportJob.ShouldSendScheduled`（日報橙燈以上、週報月報一律）決定。三個作業各自在排程作業頁開關，「立即產生」各自排隊（同一種報告同時只會有一份在產生）。
- **通用的章節＋表格**（0.10.41）：週報、月報的五節都是「重點＋表格（排好版的文字）＋說明」，報告視窗與 PDF 各寫一次通用呈現，新增一節只要在 `SystemReportPeriodicSections` 加一個函式，不必改畫面、PDF 與 DTO；舊報告的內容 JSON 沒有 `Sections` 時就是空清單，不需要 migration。
- **備份驗證照還原腳本做、但不還原**（0.10.41）：證明「還原得回來」最直接的方式是實際解開並打開資料庫，但不能碰正在用的資料庫，所以解到備份目錄的 `.work/` 暫存、做完就刪；空間不夠時回 `Unverifiable`（黃燈）而不是 `Failed`，避免把「這次沒辦法驗」誤報成「備份壞了」。只驗最新的一份（舊備份壞了不影響「出事時能不能救回」的判斷，且每份都驗太花時間）。
- **帳號的窄例外**（0.10.41，使用者 2026/11/20 決定）：季度清查與 AI 費用前幾名不列帳號就無法使用，所以只在這兩節的表格放帳號與姓名；會離開報告頁與 PDF 的管道（重點、通知、信件內文、AI 輸入）一律只用數量。PDF 因此是資安稽核的留存，月報預設保留 3 年。
- **保留天數依種類**（0.10.41）：日報看的是最近的狀況、週報看趨勢、月報含季度清查要留存給稽核，三者需要的保留期差很多，所以各自一個參數；清理與統計共用同一個條件運算式。
- **對話巡檢改用規則引擎**（0.10.42）：同樣的狀況在對話裡與報告頁得到同樣的燈號；模型只負責說明，不判燈號、不改數字。舊的「依序呼叫 9 個唯讀工具、用七段格式回覆」整段移除。
- **兩個明文例外**（0.10.42，使用者 2026/11/20 決定）：①`run_system_report` 會存一份報告卻**不經核准窗**（速查表 §6.7「Agent 工具的三條紅線」第 3 點的例外）——只寫內部報告、不寄信、不改業務資料，跳核准窗只會讓「幫我巡一下」多一步；只在巡檢回合載入，一般的查詢問題不會產生報告；新增其他「不經核准卻會寫資料」的工具要在 `AdminAssistantAgentTests` 的清單裡明確決定。②**在 HTTP 請求裡產生**（§6.34 第 7 點的例外）——對話本來就是長時間的串流（有心跳、有停止按鈕），放進排程佇列反而要另做等待與回填結果的機制。
- **區間由程式解析、年份由程式推**（0.10.42）：模型不知道今天幾號（指示不放現在時間，提示詞快取），讓它自己補年份或換算「上週」容易算錯，所以工具只收使用者說的原文；一次最多 31 天，整月以上請用月報（月報才有趨勢與章節）。
- **指定區間不寫 AI 說明**（0.10.42）：對話的回覆就是說明，再呼叫一次 AI 只是多花錢；報告頁的「AI 說明」一節寫明說明在那次對話裡。
- **畫面用的資料不經過模型**（0.10.42）：工具回給模型的文字不帶報告編號與按鈕資料，由 scoped 的 `AiAgentReportOutputs` 交給 `AiAgentChat` 加一則 `Report` 逐字稿，模型改寫或漏寫都不影響按鈕。
- 規則見速查表 §6.34。

## 八、里程碑

| 里程碑 | 內容 | 狀態 |
|---|---|---|
| M1 | 新頁面、規則引擎與日報、容量快照與預估、AI 說明、通知、系統參數、資料保留、取代「系統管理助理巡檢」、舊版巡檢報告移到本頁 | 0.10.39 已實作 |
| M2 | PDF 報告（含 AI 說明，去除敏感內容、不加密碼）、報告視窗「下載 PDF」與 Email 附件（寄信層新增通用附件能力） | 0.10.40 已實作 |
| M3 | 週報與月報（各自在排程作業頁開關，時間在系統參數、一律寄出）、趨勢比較與容量成長、備份可還原驗證（`BKP-VERIFY`）、AI 費用段落、每季帳號與權限清查（附在 1／4／7／10 月產生的月報）、各種報告各自的保留天數 | 0.10.41 已實作 |
| M4 | 系統管理助理：對話中指定區間產生報告與 PDF（「幫我巡一下系統」改用同一套規則，工具 `run_system_report`；不經核准窗、在對話請求裡產生兩個明文例外；回覆下方「查看報告」「下載 PDF」） | 0.10.42 已實作 |

## 九、規劃中需求

以下內容**尚未實作**，不屬於目前的驗收範圍。

- **候選需求**（尚未決定是否做）：清查的簽核流程、AI 模型 Deprecated 檢查、憑證到期、CPU／記憶體趨勢、每條規則個別調整門檻、在頁面上執行 VACUUM／WAL 維護。

## 十、驗收與測試

- 守門：`AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`SystemParameterTests`（每個參數鍵都要寫進日誌與設定檔說明）、`PageHelpCatalogTests`、`UserManualCoverageTests`。
- 功能測試：`SystemReportRulesTests`（每條規則的邊界、門檻來自系統參數、健康只看狀態不複製 Evidence、讀不到的來源變黃燈、通知不含 AI 說明、AI 輸入去識別）、`SystemReportSchedulingTests`（容量預估、日報時間轉 cron、程式提供的 cron 優先、驗證器擋覆寫並忽略已移除的作業）、`SystemReportServiceTests`（內容檔、查詢、快照、清理作業）、`SystemReportsApiTests`（非管理員 403、立即產生的驗證與稽核、報告不含路徑、舊版報告端點）。
- M2（0.10.40）：`SystemReportPdfBuilderTests`（每種燈號有無 AI 說明都能產生 PDF、發現很多時跨頁、檔名與標題、MIME 帶附件、沒有附件的信維持 multipart/alternative）；`EmailOutboxTests`（附件加密、重試後不變、寄出後清除、舊格式照樣寄出）；`NotificationTests`（信件選項傳遞、寄信器套用種類／附件／附註）；`SystemReportNarratorTests`（寄送附 PDF、太大或產生失敗時不附並說明）；`SystemReportsApiTests`（下載 PDF 200＋檔名＋稽核、查無 404）。
- M3（0.10.41）：`SystemReportSchedulingTests`（區間 `Containing`／`LastComplete`／`Previous`／`Quarter` 與季度判斷、週報與月報時間轉 cron〔星期日＝0、只開放 1～28 號〕、三個作業都由程式提供 cron 並擋覆寫）；`SystemReportPeriodicSectionsTests`（日報沒有章節、週報三節、季末月報五節、趨勢的變化寫法、容量成長與一年後、`BKP-VERIFY` 的燈號、帳號與姓名只在表格〔Highlights、AI 輸入、信件內文都沒有〕、清單超過 200 筆的截斷、PDF 畫得出各節）；`SystemBackupTests` 的備份可還原驗證（正常備份通過六項檢查且不留暫存檔、壓縮資料損毀回 `Failed`、manifest 的資料庫雜湊或檔案數被改過回 `Failed`、不是壓縮檔回 `Failed`、空間不夠或備份檔消失回 `Unverifiable`）；`SystemReportServiceTests` 的月報資料（AI 費用依功能／模型／使用者分組並和前一個月比較（含沒有計價的呼叫與系統排程的呼叫）、帳號清查（管理員、久未登入〔有登入紀錄看最後登入、沒有看建立時間〕、沒有兩步驟驗證〔Google 登入不算〕、本季成功的帳號與角色異動，刪除的對象顯示編號））與各種報告各自的保留天數；`SystemReportsApiTests`（立即產生：這一週、這個月、指定區間、超過保留天數 400，排入週報作業並寫 `Job.Trigger`；季末月報五節；排程的週報一律通知）；`Components/SystemReportViewTests`（報告視窗的本期重點與各節表格）。
- M4（0.10.42）：`SystemReportAssistantServiceTests`（沒講日期＝昨天到現在、各種日期寫法與推年份、1 月的跨年區間、2/29 找最近的閏年、單一天、起迄對調、迄日晚於今天／超過 31 天／超過保留天數／看不懂的日期回錯誤、剛好 31 天可以）；`SystemReportsApiTests.AssistantTool_GeneratesACustomReport_WithoutAiOrNotifications_AndAudits`（存成 `Custom`／`Assistant`、AI 說明 `Skipped` 並寫明說明在對話、不通知、寫 `SystemReport.Generate`、交出報告標題）；`AiAgentRunnerTests.ToolReport_IsAppendedBelowTheReply_AndSaved`（`Report` 逐字稿接在回覆後面並存檔）；`AdminAssistantAgentTests`（巡檢指示指向 `run_system_report`、`PatrolTools_OnlyInPatrolTurns_AndAreTheOnlyApprovalFreeWriters`、`Home` 64 個工具）；`AdminAssistantToolsTests`（登記 `ReportAssistantTools`）。實機腳本 `scripts/e2e/system-reports.mjs` R13～R15（需要真的 AI 設定）。
- 品質關卡結果見 [M1 changelog](../changelog/2026-11-20-系統巡檢報告M1.md)、[M2 changelog](../changelog/2026-11-20-系統巡檢報告M2.md)、[M3 changelog](../changelog/2026-11-20-系統巡檢報告M3.md) 與 [M4 changelog](../changelog/2026-11-23-系統巡檢報告M4.md)。

## 十一、相關程式與文件

- `src/MyProject/MyProject.Web/SystemReports/`（`SystemReportPdfBuilder`〔0.10.40〕、`SystemReportPeriodicSections`〔0.10.41〕、`SystemReportAssistantService`〔0.10.42〕、`SystemReportRules`、`SystemReportCollector`、`SystemReportFacts`〔含 `SystemReportPeriod`〕、`SystemReportGenerator`、`SystemReportSummarizer`、`SystemReportNarrator`、`SystemReportDelivery`、`SystemReportScheduleProvider`、`CapacityForecast`、`CapacityProbe`、`VolumeResolver`）
- `src/MyProject/MyProject.Web/Backup/BackupVerifier.cs`（0.10.41）
- `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/ReportAssistantTools.cs`、`Ai/Agents/AiAgentReportOutputs.cs`、`AdminAssistantAgent.PatrolTools`（0.10.42）；`MyProject.Web.Client/Views/Admins/AdminAssistantView.*`（回覆下方的報告卡片）
- `src/MyProject/MyProject.Web/Scheduling/Jobs/SystemReportDailyJob.cs`（`SystemReportJob` 與日報、週報、月報三個作業）、`CapacitySnapshotJob.cs`、`AdminPatrolReportRetentionJob.cs`、`Scheduling/IScheduledJobCronOverride.cs`
- `src/MyProject/MyProject.Web/Controllers/SystemReportsController.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/SystemReportService.cs`、`SystemReportMetricsService.cs`、`SystemReportMetricsService.Monthly.cs`（0.10.41）、`CapacitySnapshotService.cs`
- `src/MyProject/MyProject.AccessDatas/Models/SystemReport.cs`、`CapacitySnapshot.cs`；`MyProject.Models/Systems/SystemReportSettings.cs`、`SystemReportModels.cs`；`MyProject.Dtos/Admins/SystemReportDtos.cs`
- `src/MyProject/MyProject.Web.Client/Pages/SystemReportPage.razor`、`Views/Admins/SystemReportView.*`、`SystemReportDetailModal.*`、`AdminPatrolReportsModal.*`（舊版巡檢報告）、`Api/SystemReportApi.cs`
- 相關 PRD：[排程作業](排程作業-prd.md)、[系統參數](系統參數-prd.md)、[資料保留與清理](資料保留與清理-prd.md)、[系統管理助理](系統管理助理-prd.md)、[系統健康監控](系統健康監控-prd.md)、[站內通知與公告](站內通知與公告-prd.md)、[AI 提示詞管理](AI提示詞管理-prd.md)、[Token 用量](Token用量-prd.md)、[寄信服務](寄信服務-prd.md)、[稽核紀錄](稽核紀錄-prd.md)、[系統備份與還原](系統備份與還原-prd.md)（備份可還原驗證）
- 設定：[日誌與設定檔說明](../operations/日誌與設定檔說明.md)；規則：[開發慣例與限制速查](../architecture/開發慣例與限制速查.md) §6.34
