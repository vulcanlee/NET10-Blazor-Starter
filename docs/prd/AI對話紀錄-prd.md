# AI 對話紀錄 PRD

- 文件版本：1.12
- 文件狀態：已實作
- 現行系統版本：0.9.169
- 首次實作版本：0.9.72
- 最後核對日期：2026/11/07

## 一、目標與範圍

讓管理員能回頭檢視**每一次呼叫 AI API 時實際送出的 Prompt 與取得的 Response**，並知道那次呼叫屬於哪個**作業**、由哪位**使用者**發起，
用來排查 AI 結果不如預期、失敗或花費異常的原因，而不必重現一次。本功能複刻自 ReviewSkills.AI 1.0.34 的同名頁，UI 改用本專案的設計規範。

- 範圍：
  - 全部 AI 呼叫點（作業見 §3.1）的完整請求與回應記錄。內文寫在檔案系統，SQLite 只存可篩選的索引。
  - 管理員專屬頁面 `/ai-call-logs`：篩選、內文搜尋（0.9.169 起，逐檔掃描）、清單、五種檢視的明細視窗、JSON 下載、PDF 匯出（單筆與合併，合併 0.9.169 起）、CSV（僅中繼資料）。
  - 單筆刪除、清除此日之前、清空全部，以及依保留天數自動過期。
  - 與 [Token 用量](Token用量-prd.md) 以呼叫識別碼（CallId）雙向連結。
- 非範圍：
  - 不提供 Web API（與同群組的 Token 用量、日誌檢視等管理員頁面一致，只有 Blazor 畫面）。
  - 不建全文索引：內文搜尋在篩選結果內逐檔掃描，內文不複製進資料庫（見 §4.1）。
  - 不能重送或修改對話。
  - 不做「一般使用者只看自己的」分權。
  - 不做 SSE 專屬的欄位與檢視：ReviewSkills 的 SSE 相關欄位與檢視刻意不移植。0.9.141 起 AI 日誌分析與 AI 例外分析改走串流，
    串流呼叫的原始回應就是 SSE 原文，照一般原始回應保存與顯示（不是 JSON，所以不排版）。

> ⚠️ **這是對既有紅線的正式修訂。**0.9.71 之前系統承諾「絕不保存提示詞與模型回應內文」。
> 0.9.72 起修訂為：`TokenUsageLog`／`TokenUsagePath` 仍然絕不存內文；完整內文**只准**存進本功能的專屬儲存區，
> 僅管理員可看，並依保留天數自動過期。其他資料表、NLog 日誌與稽核紀錄仍不得寫入內文。
> 規則的權威來源見 [開發慣例與限制速查 §6.7](../architecture/開發慣例與限制速查.md)。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 |
| --- | --- | --- |
| `/ai-call-logs` | 系統管理 → AI 管理 → id=66「AI 對話紀錄」（AI 提示詞與 Token 用量之間，圖示 `forum`；0.9.113 前在「統計與分析」）| **管理員專屬**（`CheckIsAdmin()`；權限鍵 `角色_AI對話紀錄` 不上架角色矩陣）|
| `/ai-call-logs?callId={Guid}` | Token 用量明細窗「AI 對話紀錄」列的查看對話按鈕 | 同上；載入後自動開啟該筆明細，找不到時提示原因 |

隱私揭露：在操作說明（日誌檢視、系統例外紀錄、系統健康監控、Token 用量、本頁）中註明
「AI 呼叫內容會被記錄，管理員可查閱」，畫面不另加提示；本頁上方的說明框另標示內容敏感與保留天數。

## 三、記錄什麼

### 3.1 記錄點

| 作業（`TokenUsageOperations`）| 呼叫點 | 身分來源 | 關聯說明（RelatedInfo）|
|---|---|---|---|
| AI 日誌分析 | `AiChatCompletionClient`（經 `AiLogAnalysisService`）| `CurrentUserService` | 「日誌 起～迄，送出 n／N 筆」（實際送出的最新 n 筆）|
| AI 例外分析 | `AiChatCompletionClient`（經 `AiExceptionAnalysisService`）| 同上 | 第一次：「例外紀錄 #id（例外類型）」；追問：「例外紀錄 #id，追問第 n 輪」|
| 系統健康檢測 | `AiHealthProbe` | 同上 | 「系統健康監控 AI 連線探測」|
| Agent 測試台（0.9.142 起）| `AiAgentRunner`／`AiAgentChat` | 同上 | 「專案小幫手（範例）」（Agent 的顯示名稱）|
| 專案狀態報告、多觀點審查（0.9.144 起，Agent 工作流程）| `AiWorkflowRunner`／`AiWorkflowRun` | 同上 | 「流程／agent」，例如「專案狀態報告／撰寫」「多觀點審查／彙整」|
| 系統管理助理（0.9.155 起）| `AiAgentRunner`／`AiAgentChat`（經 `AdminAssistantTurnPlanner`）| 同上 | 「系統管理助理」|
| 系統管理助理（分派）（0.9.155 起）| `AiChatCompletionClient`（經 `AdminAssistantTurnPlanner`），與主呼叫共用同一個 `ConversationId` | 同上 | 「系統管理助理（分派）」|
| 系統管理助理（排程巡檢）（0.9.161 起）| `AdminPatrolRunner`（排程作業「系統管理助理巡檢」）| 無（沒有登入使用者）| 「系統管理助理（排程巡檢）」；`ConversationId` 是巡檢報告編號 |

0.9.140 起所有呼叫點都經 `IAiAgentFactory` 建立的 MAF agent 送出，實際寫入本功能的是 agent 管線上的記帳中介層
`AiAccountingChatClient`（單一 `finally`）；請求與回應原文由 SDK 管線原則 `AiHttpCapturePolicy` 擷取。見 [MAF AI 呼叫架構](../architecture/MAF-AI呼叫架構.md)。

AI 例外分析與它的每一輪追問共用同一個 `ConversationId`（由 `ExceptionAiAnalysisModal` 開窗時產生，經 `AiExceptionCallContext`
傳給無狀態的服務），明細窗會列出同一段對話的所有呼叫。本專案的追問與第一次分析共用作業名稱「AI 例外分析」，以關聯說明區分輪數。
Agent 測試台的一段對話同樣共用一個 `ConversationId`（開對話時產生）；0.9.143 起對話會保存，日後從「我的對話」接續時沿用同一個 `ConversationId`，前後的呼叫仍串成同一段（對話本身的保存與過期是另一套，見 [MAF AI 呼叫架構 §6.7](../architecture/MAF-AI呼叫架構.md)；刪除或過期的 Agent 對話不影響本功能的紀錄）。
Agent 工作流程（0.9.144 起）的一次執行共用一個 `ConversationId` 與一個 `RunId`：流程裡每個 agent、人工介入（工具核准、Agent 提問、審閱）停頓前後的所有呼叫都串在一起，以關聯說明區分是哪個 agent。

**同一次執行（`RunId`，0.9.142 起）**：一次執行（一次按鈕、一輪追問、Agent 的一則訊息或一次核准回覆）一個 `RunId`。
Agent 一次執行裡的工具呼叫迴圈會呼叫模型好幾次（先決定要用哪個工具、執行後再整理答案），每一次各記一筆、`RunId` 相同；
Token 用量同一次呼叫也帶同一個 `RunId`。健康探測與 0.9.141 以前的紀錄沒有 `RunId`。見 [MAF AI 呼叫架構 §6.4](../architecture/MAF-AI呼叫架構.md)。

### 3.2 何時記

**只要請求確實送出就記**：成功、上游錯誤（保存錯誤 body）、回應為空／內容過濾／長度上限、逾時、連線中斷、使用者取消、非預期錯誤。
設定不完整（根本沒送出）不記。

⚠️ Token 用量**不記**使用者取消與非預期錯誤的呼叫（沿用既有規則），所以兩頁筆數可能不同；明細窗的用量區塊會說明原因。
例外（0.9.141 起）：串流途中使用者按「停止產生」、已經收到文字時，Token 用量也記一筆 `Canceled`（用量空白），兩頁都有。

### 3.3 保存的內容

| 項目 | 說明 |
|---|---|
| 原始請求 | OpenAI 官方 SDK 產生的 JSON（0.9.140 起；之前為 `AiChatRequestFactory`），**逐字**保存（中文直接以 UTF-8 保存，0.9.139 以前的紀錄為 `\uXXXX`；畫面另行排版；省略 `"stream": false`、欄位順序與 0.9.139 以前不同，`AiCallLogMessages.Parse` 照樣解析；0.9.141 起串流呼叫帶 `"stream": true` 與 `stream_options`；0.9.149 起 Agent 測試台與 Agent 工作流程、0.9.155 起系統管理助理（分派除外）、0.9.161 起定時巡檢走 Responses API，請求是 `instructions`＋`input` 格式、帶 `"store": false`）。**不含 HTTP 標頭與金鑰** |
| 原始回應 | 回應 body（含錯誤 body）；沒拿到回應（逾時、連線失敗、取消）時為 null。串流呼叫（0.9.141 起）為 **SSE 原文**（`data: {…}` 逐行，由 `AiCapturingStream` 邊讀邊記）；中途取消或逾時時是已收到的部分 |
| 解析後的回應文字 | `choices[0].message.content`；串流為各 chunk `delta.content` 串起來；Responses API（0.9.149 起）為 `output` 裡各 `output_text` 串起來，串流取最後的 `response.completed`，沒有時串起 `response.output_text.delta`；中途取消時是**已收到的部分** |
| 中繼資料 | 送出時間（本地時間）、作業、帳號／UserId、供應商、要求模型與實際模型、端點（只含 scheme／主機／連接埠／路徑）、HTTP 狀態、結束原因、成敗與原因、例外型別、耗時、關聯說明、ConversationId、RunId（0.9.142 起）、CallId |

訊息的角色（system／user／assistant／tool）不另外存，讀取時由原始請求解析（`AiCallLogMessages.Parse`）。
Responses API 的請求（0.9.149 起）：`instructions` 為 system；`input` 為字串時是 user；`input` 陣列裡的訊息保留原角色（`developer` 當作 system）；`function_call` 顯示為 assistant「呼叫工具 名稱：參數」；`function_call_output` 為 tool（畫面與 PDF 標示「工具結果（Tool）」）。認不得的格式不顯示訊息、不丟例外。
畫面看到的永遠是實際送出的內容。

### 3.4 儲存與生命週期

- 資料表 `AiCallLog`（索引：`OccurredAt`、`CallId` 唯一、`ConversationId`）；`TokenUsageLog` 新增可空欄位 `CallId`（索引），
  migration `AddAiCallLog`。0.9.142 起兩表各加可空欄位 `RunId`（索引），migration `AddAiRunId`。
- 內容檔：`ExternalFileSystem:AiCallLogPath/{yyyyMM}/{CallId:N}.json`，一律經 `AiCallLogFileStore`。
- 寫入順序：先寫檔再建資料列；資料列建不起來就刪檔。寫檔失敗時仍建資料列（`ContentFile` 為 null，明細顯示檔案不存在）。
- 設定 `AiCallLogSettings { Enabled = true, RetentionDays = 90 }`（`RetentionDays` 範圍 1～3650，`ValidateOnStart`）：
  - `Enabled=false` 不記。
  - 排程作業「AI 對話紀錄清理」（0.9.96 起預設每天 03:00；之前是 `AiCallLogRetentionWorker` 啟動時與每 24 小時）刪除過期資料列與檔案，並清掉門檻月份之前的整個月份目錄（收拾孤兒檔）；刪到資料時寫 `AiCallLog.AutoPurge`。
  - 停用時仍執行過期清除。
  - 兩個值都可在「系統參數」頁「資料保留」分類覆寫（「AI 對話紀錄保留天數」「記錄 AI 對話內容」）。
- 系統備份預設**不含**內容檔（`BackupSettings:IncludeAiCallLogs` 預設 `false`，系統參數「備份 AI 對話內容」）：內容敏感、備份沒有加密；換主機還原後資料列還在，明細只看得到中繼資料（「內容檔不存在…」）。見 [系統備份與還原 PRD](系統備份與還原-prd.md)。
- 上限：關聯說明截斷至 500 字元。
- 記錄**絕不影響** AI 呼叫本身：recorder 全程吞例外，呼叫點另以 `RecordSafelyAsync` 在 `finally` 中執行。

## 四、畫面

UI 依本專案的設計規範：說明框沿用 Token 用量頁的淡色框、按鈕一律 `ToolbarIconButton`／`CrudActionButton`、
狀態一律 `StatusPill`、確認窗一律 `ConfirmDialog`、顏色只用 `theme.css` 的 token。

### 4.1 清單

- 篩選：起始日、結束日、作業、結果（成功／失敗或取消）、模型、帳號（部分比對）、關聯說明關鍵字（部分比對）。
- **內文搜尋**（0.9.169 起，`AiCallLogService.FindContentMatchesAsync`）：
  - 「內文關鍵字」有填時，按「查詢」或「重新整理」先套用上面的篩選，依送出時間由新到舊取最多 `ContentScanLimit`（1,000）筆，逐一讀內容檔比對，不分大小寫。
  - 比對畫面看得到的文字：每則送出的訊息（`AiCallLogMessages.Parse`）與回應文字。原始 JSON 的欄位名稱不算；內容檔不存在的略過。
    - 請求解析不出訊息（不認得的格式）時改比對原始請求。
    - 呼叫**失敗**而沒有回應文字時改比對原始回應（錯誤 body）；成功但只有工具呼叫的回應不比對原始 JSON。
  - 掃描在背景執行緒進行，離開頁面時取消；掃描期間停用「查詢」「重新整理」。
  - 搜尋之後改了任何條件（含內文關鍵字）卻沒按「查詢」，清單上方提示結果已過期，匯出 CSV／PDF 前也先請使用者重新查詢。
  - 結果以 Id 清單交回查詢（`AiCallLogQuery.Ids`）：換頁、排序、CSV 與合併 PDF 匯出都用這份清單，不重新掃描。
  - 清單上方顯示「內文搜尋「…」：掃描 N 筆，符合 M 筆。」；篩選結果超過上限時加註只掃描最新的 1,000 筆、請縮小範圍。
  - ⚠️ 不建索引：內文只准存在內容檔（§一的紅線），也不進系統備份的資料庫。
  - 稽核：`AiCallLog.Search` 多一個條件 `content`（原文只進詳細內容檔，摘要只記有沒有填）。
- 欄位：
  - 送出時間（可排序，預設新到舊）
  - 結果（`StatusPill`：成功／失敗（原因）／已取消）
  - 作業、發起者、模型
  - 耗時（可排序）、送出字元（可排序）
  - 關聯說明
  - 操作：查看對話、刪除
- 工具列：查詢、重新整理、匯出 CSV（上限 10,000 筆，**只含中繼資料**）、匯出目前查詢結果（PDF，0.9.169 起）、清空全部；另有「清除此日之前＋批次清除」，勾選後出現「刪除選取」與「合併匯出 PDF（N）」（0.9.169 起）。
- 破壞性動作一律 `ConfirmDialog.AskDestructiveAsync` 二次確認。
- 刪除、清除、清空與匯出都寫自我稽核（`AiCallLog.Delete`／`Purge`／`ClearAll`／`Export`），只記動作、Id 與筆數；合併匯出記 `format=pdf-merged; source=selected|conversation|query; rows=N`，匯出了哪幾筆（`ids=…`）放在稽核的詳細內容檔。

### 4.2 明細視窗（`.ai-call-log-modal`，96vw／96vh，0.9.75 起）

- **一般／中／大**：窗內字級三段縮放（`--ai-font-scale`），與日誌檢視、例外 AI 分析兩個視窗一致；關窗回到「一般」。
- **上方資料**：
  - 基本：送出時間、作業、發起者、結果與例外型別、供應商／模型（要求→實際）、端點、HTTP／結束原因、耗時、關聯說明、呼叫識別碼；有 `RunId` 時多一列「執行編號」（0.9.142 起）。
  - **用量與費用**：以 CallId 帶出 Token 用量的輸入／輸出／合計與 NT$／US$。
- **同一段對話**：ConversationId 相同的其他呼叫，可點選切換。
- **同一次執行**（0.9.142 起）：RunId 相同的呼叫超過一筆時列出「同一次執行（共 N 次模型呼叫）」，依時間由舊到新，每列顯示時間與結束原因（沒有時顯示作業），目前這筆標「（目前這筆）」，其他筆可點選切換。只有一筆時不列。CSV 匯出欄位不變。
- **五種檢視**（只渲染目前這一種）：
  1. 完整對話：依序列出送出的每則訊息，最後是 AI 回應。
  2. 送出 Prompt：只列送出的訊息。
  3. 取得 Response：只列 AI 回應；未取得時附說明。
  4. 原始 Request：排版後的 JSON。
  5. 原始 Response：排版後的 JSON body；沒有 body 時說明原因。
- **每段內容**：
  - 淺色卡片，左側色條標示角色：系統提示（System，灰）／使用者（User，重點色）／AI 助理（Assistant，綠）；Agent 的工具結果標示「工具結果（Tool）」（0.9.149 起）。
  - 以 `StatusPill` 標示方向：送出／取得。追問時的歷史回答標示「送出（對話歷史）」。
  - 附字元數與複製鈕（複製完整內容）。
  - 原始 Request／Response 用深色原文區塊（`--app-code-*`，與日誌檢視、健康監控、例外堆疊同一組）。
- **純文字／Markdown 切換**：預設純文字。Markdown 一律經 `AiMarkdownRenderer.ToSafeHtml`，渲染結果超過 512 KB 時退回純文字。
- **顯示上限**：每段畫面最多顯示 20 萬字元，超過時提示改用複製或下載 JSON。
- **下載完整紀錄（JSON）**：內容檔原文（不加 BOM）。
- **匯出 PDF**：`AiCallLogPdfBuilder`（沿用 `AiMarkdownPdfRenderer`），中繼資料加完整對話，**不含原始 JSON**。每段上限 20 萬字元，超過時註明截斷。
- **匯出整段對話 PDF**（0.9.169 起）：同一段對話有兩筆以上時出現，把整段合併成一份 PDF（見 §4.4）。

### 4.3 Token 用量頁的連結

Token 用量明細窗新增「AI 對話紀錄」列：
- 有對應紀錄時顯示查看對話按鈕，導向 `/ai-call-logs?callId=`。
- 否則顯示「（0.9.72 之前的紀錄沒有對話可看）」或「（對話紀錄已清除，或當時未啟用記錄）」。

深連結帶查詢字串，所以 `MainLayout`／`NavMenu` 比對選單前會先去掉 `?` 與 `#`（頁名與選單高亮才會正確）。

### 4.4 合併匯出 PDF（0.9.169 起）

- 三個入口：清單勾選後的「合併匯出 PDF（N）」、明細窗的「匯出整段對話 PDF」、工具列的「匯出目前查詢結果（PDF，最多 50 筆）」（有內文搜尋時只含符合的）。
- `AiCallLogPdfBuilder.BuildMerged`：開頭是標題「AI 對話紀錄（共 N 筆）」、系統／產生時間／匯出者／筆數／送出時間範圍、免責說明與索引（序號、送出時間、作業、發起者、結果）；之後每筆換頁一節（中繼資料＋完整對話），依送出時間由舊到新。
- 上限：一次最多 `MaxMergedRecords`（50）筆，超過時畫面提示縮小範圍；每段內容最多 `MaxMergedBlockCharacters`（5 萬字元），整份最多 `MaxMergedTotalCharacters`（100 萬字元；Agent 對話每次重送整段歷史，整段對話合併時會隨呼叫次數平方成長），超過時註明「完整內容請開啟單筆紀錄或下載 JSON」，整份用完後的段落只留一行註明。單筆 PDF 維持 20 萬字元。
- 內容檔不存在的那一筆仍列入，註明只有中繼資料。資料由 `AiCallLogService.GetExportItemsAsync` 一次帶出訊息、回應與用量。

## 五、驗收

| 項目 | 守門 |
|---|---|
| 記錄、停用、寫檔失敗、截斷、DB 失敗不丟例外、篩選、排序分頁、明細（訊息／用量／對話／同一次執行）、刪除、清除、清空、過期與孤兒目錄 | `AiCallLogServiceTests` |
| Agent 工具呼叫迴圈每次模型呼叫各記一筆且 RunId 相同（0.9.142 起） | `AiAgentRunnerTests` |
| 工作流程一次執行所有 agent 共用同一個 RunId 與 ConversationId、關聯說明為「流程／agent」（0.9.144 起） | `AiWorkflowRunnerTests` |
| 結果預設值、送出旗標、記錄失敗不外拋、端點去除查詢字串、訊息解析 | `AiCallCaptureTests` |
| 成功／錯誤 body／逾時／傳輸失敗／取消／非預期錯誤／未設定不記、CallId 與 Token 用量一致、內容不含金鑰、recorder 丟例外不影響結果 | `AiChatCompletionClientTests`、`AiLogAnalysisServiceTests`、`AiHealthProbeTests` |
| SDK 產生的請求 body 仍能解析成訊息、自己的逾時記一筆（0.9.140 起） | `AiAgentPipelineTests` |
| 串流：成功、取消前／後都記一筆且回應文字為已收到的部分、上游錯誤、逾時（0.9.141 起） | `AiStreamingTests` |
| 例外分析與追問共用 ConversationId、RelatedInfo | `AiExceptionAnalysisServiceTests` |
| PDF 產出、中繼資料、角色標示；合併匯出的索引順序、筆數上限、每段 5 萬字截斷（0.9.169 起） | `AiCallLogPdfBuilderTests` |
| 內文搜尋（訊息與回應、失敗比對錯誤 body、欄位名稱不算、套用篩選、略過缺檔、掃描上限）、`Ids` 篩選、合併匯出資料（0.9.169 起） | `AiCallLogServiceTests` |
| 設定預設值與範圍 | `AiCallLogSettingsTests` |
| 管理員專屬、選單一致、圖示、操作說明、資料服務生命週期、字級縮放、設定文件 | `AdminOnlyPermissionTests`、`MenuPermissionConsistencyTests`、`MenuIconTests`、`PageHelpCatalogTests`、`DataAccessServiceLifetimeTests`、`AiModalStyleConventionTests`、`DocumentationConventionTests` |

## 六、已知限制

- 內文搜尋逐檔掃描，一次最多掃描篩選結果中最新的 1,000 筆；範圍大時會比一般查詢慢，較舊的要縮小範圍再查。
- 合併匯出一次最多 50 筆，每段內容最多 5 萬字元、整份最多 100 萬字元。
- AI 日誌分析的關聯說明是「實際送出的日誌」時間區間，不含日誌檢視頁當時的查詢條件。
- 日誌分析的單一內容檔可能數 MB；畫面與 PDF 以上限保護，完整內容請下載 JSON。
- 每次開啟系統健康監控都會新增一筆「系統健康檢測」紀錄（內容很小，可用作業篩掉，也會自動過期）。
- 內容檔寫在 AI 呼叫回到畫面之前；日誌分析送出大量日誌時，會多出一次寫檔的時間。

## 七、規劃中需求

目前沒有。（0.9.169 已提供內文搜尋與多筆合併匯出 PDF。）
