# MAF AI 呼叫架構

- 文件版本：1.2
- 文件狀態：第一、二階段與第三階段 A 已實作（第三階段 B、第四階段規劃中）
- 現行系統版本：0.9.142
- 首次實作版本：0.9.140
- 最後核對日期：2026/11/03

> 系統內**所有** LLM 呼叫都經由 Microsoft Agent Framework（MAF）建立的 agent 送出（0.9.140 起）。
> 本文件說明管線怎麼組、記帳點在哪裡、為什麼這樣決定、Agent 骨架怎麼用（§6），以及後續階段的範圍。
> 新增 LLM 呼叫點的紅線仍以 [開發慣例與限制速查 §6.7](開發慣例與限制速查.md) 為準。

---

## 1. 為什麼改用 MAF

0.9.139 以前，AI 日誌分析、AI 例外分析與健康探測都是手刻 HTTP（自己組 JSON、自己 POST `/chat/completions`）。
這對「送一段對話、拿一段回答」夠用，但腳手架與從它複刻出的專案（例如交接知識助理）接下來要做的是
**Agent**：會呼叫工具、會多輪對話、會多個 agent 串成工作流程。這些能力 MAF 都有現成的，手刻 HTTP 沒有。

前提是**現有功能不能壞**，速查表 §6.7 的記帳紅線要完整保留。所以第一階段只換底層，畫面、服務介面、
記帳結果、錯誤訊息都維持原樣，並以既有測試證明。

## 2. 管線

```
呼叫端（AiChatCompletionClient／AiHealthProbe／AiAgentRunner）
  │  IAiAgentFactory.CreateAgent(AiCallContext, instructions, tools)   ← 唯一入口
  ▼
ChatClientAgent（MAF）
  │  MAF 自動加上的預設管線：核准綁定 → FunctionInvokingChatClient（工具呼叫迴圈）→ …
  ▼
AiAccountingChatClient（記帳中介層）                       ← 每一次模型呼叫都經過這裡
  │  用量上限 → AiCallCapture → 逾時（CancelAfter）→ Token 用量 → AI 對話紀錄（單一 finally）
  ▼
Microsoft.Extensions.AI 的 OpenAI 轉接 → OpenAI 官方 SDK
  │  管線原則：AzureApiKeyPolicy（只有 Azure）→ AiHttpCapturePolicy（擷取請求／回應原文）
  ▼
具名 HttpClient「AiChatCompletions」（IHttpClientFactory，逾時無限）→ Azure OpenAI v1／OpenAI
```

- **記帳點在工具呼叫迴圈下方**：agent 一次執行可能呼叫模型很多次（每次工具呼叫後都要再問一次模型），
  每一次都經過 `AiAccountingChatClient`，所以不會漏帳。`clientFactory` 掛上去的元件本來就落在 MAF 的
  `FunctionInvokingChatClient` 底下；**不要**自己呼叫 `UseFunctionInvocation()`，否則順序要自己控制。
- **記帳結果從 HTTP 原文解析**：用量、實際模型名稱、`finish_reason`、Azure 內容過濾類別都由
  `AiChatResponseParser` 從 `AiHttpCapturePolicy` 擷取的回應原文解析，不用 SDK 轉換後的物件。
  這樣 Token 用量與 AI 對話紀錄的內容和 0.9.139 以前逐欄位一致，`RawUsageJson` 也仍只有 usage 子物件。
- **SDK 讀不懂、但 HTTP 成功的回應**（例如 Azure 的 `choices: []`）：上游已計費，記帳中介層改用自己的解析器照常記帳，
  不當成非預期錯誤丟掉這筆帳。
- **失敗**在記帳之後以 `AiCallException`（帶原因、給使用者看的訊息、HTTP 狀態、上游 error code）往外丟，
  呼叫端再轉成自己的結果型別（`AiAnalysisResult`、`AiHealthProbeResult`）。
- **串流**（0.9.141 起，第二階段）：`GetStreamingResponseAsync` 與非串流共用同一套記帳規則（用量上限、逾時、錯誤對應、
  空回應、單一 `finally` 記 AI 對話紀錄），差別如下。第一階段（0.9.140）記帳中介層曾以 `NotSupportedException` 明確拒絕串流。
  - **擷取**：串流回應 SDK 不緩衝，所以 `AiHttpCapturePolicy` 在 2xx 串流回應上把 `ContentStream` 換成 `AiCapturingStream`，
    SDK 讀到哪裡就同時寫進 `AiCallCapture.StreamBuffer`；串流結束或中途取消時 `CompleteStreamCapture()` 轉成 `ResponseBody`，
    所以 AI 對話紀錄的原始回應是 **SSE 原文**（取消時是已收到的部分）。錯誤回應不換，SDK 自己整段讀出來組例外。
  - **解析**：用量、實際模型、`finish_reason`、過濾類別在串流**結束後**由 `AiChatResponseParser.ParseStream` 從 SSE 原文解析
    （逐行讀 `data:`，`[DONE]` 與不完整的行略過）；`ExtractStreamUsageJson` 只回帶 usage 那個 chunk 的 usage 子物件，紅線同非串流。
    請求由 SDK 帶 `"stream": true` 與 `stream_options.include_usage`，usage 在最後一個 chunk（`choices` 為空）。
  - **取消**：已經收到文字才取消 → Token 用量記一筆 `Canceled`、用量空白（有次數、沒金額；已產生的字上游照樣計費，usage 卻要等串流完整結束才拿得到）；
    還沒收到文字就取消（以及所有非串流的取消）照舊不記用量。AI 對話紀錄兩種都記，回應文字是已收到的部分；
    日誌為 Information `AI chat completion canceled by the caller. Operation=…, ReceivedCharacters=…, ElapsedMs=…`。
    呼叫端沒取消、只是中途停止列舉，比照取消記錄。
  - **逾時**：`AiCallContext.Timeout`（`AiSettings.TimeoutSeconds`）涵蓋**整段**串流，不是「等第一個字」。
  - **呼叫端**：`IAiChatCompletionClient.StreamAsync` 回傳 `IAsyncEnumerable<AiStreamUpdate>` —— 一段段 `TextDelta`，最後**恰好一筆**帶 `Result`；
    不丟例外，取消時最後一筆是 `Canceled`，已收到文字則 `IsInterrupted = true`、`Markdown` 為已收到的部分。
    `IAiLogAnalysisService.AnalyzeStreamingAsync`、`IAiExceptionAnalysisService.AskStreamingAsync` 包在它外面；非串流版保留。

## 3. 技術決定

| 決定 | 理由 |
|---|---|
| 沿用 **Chat Completions**，不用 Responses API | AI 對話紀錄頁、PDF、`AiCallLogMessages.Parse` 都讀 Chat Completions 格式；Responses API 預設在 OpenAI 端保存對話，企業機敏資料較不放心 |
| Azure 也用 `OpenAI` 官方 SDK（指向 `{Endpoint}/openai/v1`），**不用** `Azure.AI.OpenAI` | 後者新版（2.9.0-beta）仍是預覽；v1 端點用 OpenAI SDK 即可 |
| Azure 一律送 **`api-key` 標頭**（`AzureApiKeyPolicy` 移除 `Authorization`）| SDK 預設送 Bearer，官方沒有明文保證 Azure v1 接受；改送 `api-key` 和 0.9.139 以前完全一致 |
| SDK **重試設 0**（`ClientRetryPolicy(maxRetries: 0)`）| SDK 預設對 408／429／5xx 重試 3 次，每次都可能計費卻只記一筆帳；也會掩蓋設定錯誤 |
| SDK `NetworkTimeout` 與具名 HttpClient 的 `Timeout` 都設**無限**，逾時由記帳中介層 `CancelAfter` 控管 | SDK 預設每次網路操作 100 秒，會砍掉想比較久的推論模型；自己控管才分得出「使用者取消」與「逾時」，健康探測也能有自己的 30 秒 |
| `AiHttpCapturePolicy` 放在 **SDK 管線**而不是 HttpClient 的 DelegatingHandler | 管線是我們在 `AiAgentFactory` 自己組的，正式環境與測試（假的 `HttpMessageHandler`）走同一條路 |
| 每個作業各建一個 agent，`AiCallState` 交接「目前這一次呼叫」的擷取物件 | 同一個 agent 的模型呼叫是循序的，不需要 AsyncLocal |

套件版本集中在 `src/MyProject/Directory.Packages.props`：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.OpenAI` 1.27.0、
`Microsoft.Extensions.AI.OpenAI` 10.11.0、`OpenAI` 2.14.0，只有 `MyProject.Web` 參考。

## 4. 與 0.9.139 的差異（第一階段）

畫面、服務介面、`AiChatCompletionRequest`／`AiAnalysisResult`、錯誤訊息文字、Token 用量與 AI 對話紀錄的內容都不變。
看得到的差異只有：

| 項目 | 0.9.139 | 0.9.140 |
|---|---|---|
| 請求 body 的 `stream` 欄位 | 明寫 `"stream": false` | 省略（預設即不串流，語意相同）|
| 請求 body 的欄位順序與中文 | 自己用 `Utf8JsonWriter` 寫，中文跳脫成 `\uXXXX` | 由 SDK 產生，中文直接以 UTF-8 保存（AI 對話紀錄頁照樣解析）|
| 健康探測失敗的 Token 用量／對話紀錄「失敗原因」| 連線失敗記 `TransportError`、任何 HTTP 失敗一律 `UpstreamError` | 與分析共用分類：連線失敗 `UpstreamError`、401 `Unauthorized`、429 `RateLimited`、404 `NotConfigured` |
| 失敗時寫日誌的記錄器 | `MyProject.Web.Ai.AiChatCompletionClient`、`MyProject.Web.Ai.AiHealthProbe` | 呼叫層級的訊息改由 `MyProject.Web.Ai.AiAccountingChatClient` 寫；探測另寫一行摘要 |

### 4.1 與 0.9.140 的差異（第二階段：串流）

| 項目 | 0.9.140 | 0.9.141 |
|---|---|---|
| AI 日誌分析、AI 例外分析（含追問）| 等整份回覆回來才顯示 | 逐段顯示（約每 150 ms 經 `AiMarkdownRenderer.ToSafeHtml` 重轉一次、超過 512 KB 停止更新）；產生中顯示「產生中…已 N 秒」與「停止產生」，複製與 PDF 停用 |
| 停止 | 只能關窗（放棄、丟棄）| 「停止產生」保留已收到內容並加註「⚠️ 本內容於產生途中由使用者停止，可能不完整。」（`AiInterruption.Notice`，畫面、複製、PDF 都有，不送回給 AI）；關窗仍是放棄 |
| 請求 body | 省略 `stream` | 畫面的呼叫帶 `"stream": true` 與 `stream_options.include_usage`（SDK 產生）；健康探測仍不串流 |
| AI 對話紀錄的原始回應 | 完整 JSON | 串流呼叫為 SSE 原文；取消時是已收到的部分，回應文字也是已收到的部分 |
| Token 用量 | 取消一律不記 | 串流途中已收到文字才取消 → 記一筆 `Canceled`、用量空白；頁面以灰色「已取消」標籤顯示（不是紅色「失敗」）|
| 取消的日誌 | `Operation`、`ElapsedMs` | 多 `ReceivedCharacters` |
| 日誌檢視 AI 失敗提示的記錄器名稱 | 仍寫 `MyProject.Web.Ai.AiChatCompletionClient`（第一階段漏改）| `MyProject.Web.Ai.AiAccountingChatClient` |

## 5. 新增 LLM 呼叫點

- 「使用者按按鈕、送一段對話」：照舊用 `IAiChatCompletionClient`，只給 `Operation` 與三段情境化提示文字。
- 其他情境：注入 `IAiAgentFactory`，`CreateAgent(new AiCallContext { Operation, Timeout, … })` 後用 MAF 的
  `RunAsync` 執行，`catch (AiCallException)` 轉成自己的結果。記帳、對話紀錄、用量上限都自動套用。
- ⚠️ **不要**自己 `new OpenAIClient`、`AsIChatClient()` 或直接用 `HttpClient` 打 API —— 那等於繞過記帳（§6.7）。
- 作業名稱仍要登記在 `TokenUsageOperations`，模型費率仍要補在 `AiPricingSettings.Models`。
- 會呼叫工具、多輪對話的功能：寫成 Agent（§6）。

## 6. Agent 骨架（第三階段 A，0.9.142 起）

第三階段拆成兩步：**A** 先把 agent 的定義方式、工具權限、核准、記帳串接（RunId）與各功能的模型做出來，並以「Agent 測試台」頁驗收；
**B** 再做對話保存（§7）。0.9.142 的對話只存在記憶體，離開或重新整理頁面就沒了。

### 6.1 定義

Agent 一律寫在 **C# 程式碼**（不在畫面上設定），提示詞沿用「AI 提示詞」頁管理。

| 型別 | 欄位 | 說明 |
|---|---|---|
| `AiAgentDefinition` | `Name`、`DisplayName`、`Operation`、`PromptTemplateKey`、`ModelKey`、`Tools`、`TimeoutSeconds` | `Name` 是稽核紀錄的 agent 欄位；`Operation` 是 `TokenUsageOperations` 常數；`ModelKey` 是 `nameof(AiModelSettings.屬性)`（null＝預設模型）；`TimeoutSeconds` 為 null 時用 `AiSettings.TimeoutSeconds` |
| `AiToolDefinition` | `Name`、`DisplayName`、`Description`、`PermissionPage`、`PermissionAction`、`IsMutating`、`Create` | `Name` 是給模型看的函式名稱（snake_case）；權限兩欄**必填**；`Create` 每段對話從當下的 scope 建出實際的 `AIFunction` |

範例是 `AgentPlaygroundAgent`（「專案小幫手（範例）」，作業「Agent 測試台」）與它的兩個工具 `ProjectAgentTools`：

| 工具 | 權限 | 異動 | 做什麼 |
|---|---|---|---|
| `search_projects`（搜尋專案）| 專案項目：檢視（`專案項目:view`）| 否 | 呼叫 `ProjectService.GetAsync` 依關鍵字搜尋，最多 10 筆，回 Id、名稱、狀態與截斷到 200 字的描述 |
| `update_project_description`（修改專案描述）| 專案項目：編輯（`專案項目:edit`）| **是** | 呼叫 `ProjectService.GetAsync`／`UpdateAsync`；看不到的專案回「找不到或沒有權限」，不洩漏是否存在 |

工具**一律呼叫既有服務**：團隊範圍、`Project.Update` 稽核與並行版本號照常生效，工具本身不重寫商業規則。

### 6.2 工具的權限與稽核

`AiAgentToolFactory.Create(定義)` 把每個 `AiToolDefinition` 包成交給 agent 的工具：

1. **`PermissionCheckedAIFunction`**（每個工具都包）：執行前由 `IAiToolPermissionGuard.CanInvokeAsync` 以**目前登入的使用者**檢查
   `PermissionPage`＋`PermissionAction`（經 `IPermissionChecker`，管理員短路、未登入一律否）。
   - 沒有權限 → **不執行**，回一段「使用者沒有執行這個操作的權限…」給模型（讓它告訴使用者去申請），稽核 `Ai.ToolDenied`。
   - 執行成功 → 稽核 `Ai.ToolInvoked`（成功）；工具丟例外 → 回一段「執行失敗」給模型而不讓整次執行失敗，稽核 `Ai.ToolInvoked`（失敗，`error=例外型別`）。
2. **`ApprovalRequiredAIFunction`**（MAF 內建，只包 `IsMutating` 的工具）：模型要求呼叫時，執行停下來等使用者核准。

稽核的 detail 只有 `agent=…; tool=…（顯示名稱）; permission=頁面:動作`，`TargetType = AiTool`、`TargetId = 工具名稱`；
**不記參數與回傳內容**（那些只在 AI 對話紀錄的內容檔）。⚠️ 不要把 `AIFunctionFactory.Create` 建出來的函式直接交給 agent，那會繞過權限與稽核（速查表 §6.7）。

### 6.3 執行與核准

畫面注入 `IAiAgentRunner`：`StartAsync(定義)` 取提示詞（含固定附加的安全規則，開對話時取一次）、解析模型、經 `IAiAgentFactory` 建 agent 與 MAF session，
回傳一段 `AiAgentChat`（`ConversationId` 即 AI 對話紀錄的對話識別碼）。之後：

- `SendAsync(訊息)`、`RespondAsync(核准的 RequestId)` 各是**一次執行**，回傳 `IAsyncEnumerable<AiAgentUpdate>`，每筆只有一種：
  `TextDelta`（文字片段）、`ToolActivity`（工具開始／結束，畫面顯示工具名稱）、`Approvals`（停下來等核准，之後不會再有 `Result`）、`Result`（結束：成功、失敗或取消，沿用 `AiAnalysisResult`）。不丟例外。
- 模型要求呼叫異動工具時，執行器**先替使用者檢查權限**：沒有權限的直接以「沒有權限」拒絕、稽核 `Ai.ToolDenied`（`stage=approval`），**不跳核准窗**；
  全部都被自動拒絕時直接把拒絕送回去繼續執行，讓 agent 自己說明。有權限的才以 `Approvals` 回報給畫面。
- `RespondAsync`：清單裡的核准，其餘一律視為拒絕並稽核 `Ai.ToolRejected`；核准後工具執行時仍會再經 §6.2 的權限檢查。
- 有待核准的工具時不能 `SendAsync`（丟 `InvalidOperationException`）；同一段對話一次只能有一個執行（不是執行緒安全，畫面在執行中停用送出）。

### 6.4 RunId：同一次執行的多次模型呼叫

一次執行可能呼叫模型很多次（模型要求工具 → 執行工具 → 再問一次模型）。0.9.142 起 `AiCallLog` 與 `TokenUsageLog` 加可為 null 的 `RunId` 欄位（含索引，migration `AddAiRunId`）：

- `AiAgentChat` 每次執行前把 `AiCallState.RunId` 換成新值；`AiChatCompletionClient` 每次呼叫（一次按鈕、一輪追問）在 `AiCallContext.RunId` 帶新值；
  記帳中介層取 `state.RunId ?? context.RunId` 寫進每一筆 Token 用量與 AI 對話紀錄。健康探測沒有 RunId；0.9.141 以前的紀錄為 null。
- 「AI 對話紀錄」明細顯示「執行編號」，同一次執行超過一筆時列出「同一次執行（共 N 次模型呼叫）」，可點開其他筆。CSV 匯出欄位不變。
- 記帳規則不變：每一次模型呼叫各記一筆、各自檢查用量上限。

### 6.5 各功能的模型（`AiModelSettings`）

新增設定區段 `AiModelSettings`（`LogAnalysis`、`ExceptionAnalysis`、`AgentPlayground`），**空字串＝`AiSettings.Model`**，可在系統參數頁「AI 模型」修改。
呼叫端在 `AiCallContext.ModelKey`（經 `AiChatCompletionClient` 的走 `AiChatCompletionRequest.ModelKey`）帶 `nameof(AiModelSettings.屬性)`，由 `AiAgentFactory.CreateAgent` 以 `AiModelSettings.Resolve` 解析
（`IOptionsMonitor`，下一次建立 agent 即生效；Agent 測試台在每段對話的第一則訊息建立 agent，所以進行中的對話沿用原本的模型）。
端點與金鑰共用 `AiSettings`，只能換同一個服務裡的模型；不認得的 `ModelKey` 是程式錯誤，丟 `InvalidOperationException`。
「系統健康監控」的「AI 計費表」檢查預設模型與每一個有覆寫的模型（`OverriddenModels()`），任一查無費率即降級。設定說明見 [日誌與設定檔說明 §4.17](../operations/日誌與設定檔說明.md)。

### 6.6 新增一個 Agent

1. `AiAgentDefinition` ＋ 每個工具一個 `AiToolDefinition`（權限必填、異動工具設 `IsMutating`；工具方法呼叫既有服務、回給模型的欄位精簡），工具類別註冊到 DI。
2. `PromptTemplateKeys` 加種類（含 `All`）、`AiPromptGuardrails` 補內建提示詞與「不可信內容」說法、`PromptTemplateView` 補分頁名稱。
3. `TokenUsageOperations` 加作業名稱；新模型補 `AiPricingSettings.Models` 費率。
4. `AiModelSettings` 加屬性 → `SystemParameterCatalog.All` 加一行 → appsettings.json 加鍵 → [日誌與設定檔說明 §4.15](../operations/日誌與設定檔說明.md) 加一列。
5. 頁面用 `IAiAgentRunner`；頁面權限照新增頁面的四處同步，會花 AI 費用的頁面加進 `RolePermissionService.NotGrantedToDefaultRole`（不自動開給預設角色），並寫操作說明。

步驟細節見 [腳手架開發指引 §3.11](../guides/腳手架開發指引.md)「新增一個 Agent」。

## 7. 後續階段

| 階段 | 範圍 | 狀態 |
|---|---|---|
| 一 | 換底層，行為不變（本文件）| 0.9.140 已實作 |
| 二 | 串流：記帳中介層支援串流（串流結束取用量；已收到內容才取消 → 記一筆「已取消」、用量空白）；AI 日誌分析與 AI 例外分析改為逐段顯示，加「停止產生」，停止後保留已產生內容並標示已中止（§2、§4.1）| 0.9.141 已實作 |
| 三 A | Agent 骨架：C# 定義 agent、每個功能可指定模型（`AiModelSettings`）；工具呼叫逐次記帳並以 RunId 串起（`AiCallLog`／`TokenUsageLog` 加欄位）；工具依使用者 RBAC 執行、會改資料的工具強制核准；以「Agent 測試台」頁驗收，對話只在記憶體（§6）| 0.9.142 已實作 |
| 三 B | 對話保存：多輪對話存檔案系統、SQLite 只放中繼資料（本人＋管理員可見、保存天數可設定），可接續之前的對話 | 規劃中 |
| 四 | 多 Agent 工作流程（MAF Workflows）：所有 agent 經 `IAiAgentFactory` 建立、共用一個 RunId；人工介入當下回應，不做跨重開機檢查點 | 規劃中 |

共同原則：非結構、會佔大空間的資料（對話內容、工具參數與結果）一律存檔案系統，資料庫只放可查詢的中繼資料；
工具參數與結果只進 AI 對話紀錄檔，稽核只記工具名稱、使用者、成敗與是否被擋。

## 8. 程式位置

| 檔案 | 職責 |
|---|---|
| `MyProject.Web/Ai/AiAgentFactory.cs` | `IAiAgentFactory`：組 OpenAI SDK（端點、重試、逾時、傳輸、管線原則）並以 `AsAIAgent(..., clientFactory:)` 掛上記帳中介層；0.9.142 起接受工具、依 `ModelKey` 解析模型 |
| `MyProject.Web/Ai/AiAccountingChatClient.cs` | 記帳中介層：用量上限、逾時、錯誤對應、Token 用量、AI 對話紀錄（非串流與串流共用，0.9.141 起含 `GetStreamingResponseAsync`）|
| `MyProject.Web/Ai/AiPipelinePolicies.cs` | `AiHttpCapturePolicy`（擷取原文）、`AzureApiKeyPolicy`（Azure 認證標頭）、`AiCapturingStream`（串流回應邊讀邊記，0.9.141 起）|
| `MyProject.Web/Ai/AiChatResponseParser.cs` | 從回應原文解析用量、模型、`finish_reason`、過濾類別；`ParseStream`／`ExtractStreamUsageJson` 解析 SSE（0.9.141 起）|
| `MyProject.Web/Ai/AiInterruption.cs` | 「產生途中被使用者停止」的註記文字與 `Annotate`（只用在畫面、複製、PDF）|
| `MyProject.Web/Ai/AiCallContext.cs` | `AiCallContext`（0.9.142 起含 `RunId`、`ModelKey`）、`AiCallException`、`AiCallState`（0.9.142 起含 `RunId`）、`AiCallOutcome` |
| `MyProject.Web/Ai/Agents/AiAgentDefinition.cs` | `AiAgentDefinition`、`AiToolDefinition`（0.9.142 起）|
| `MyProject.Web/Ai/Agents/AiAgentToolFactory.cs` | `AiAgentToolFactory`、`IAiToolPermissionGuard`／`AiToolPermissionGuard`（權限與稽核）、`PermissionCheckedAIFunction`（0.9.142 起）|
| `MyProject.Web/Ai/Agents/AiAgentRunner.cs` | `IAiAgentRunner`、`AiAgentChat`（執行、RunId、核准分流）、`AiAgentUpdate`、`AiToolActivity`、`AiToolApprovalRequest`（0.9.142 起）|
| `MyProject.Web/Ai/Agents/AgentPlaygroundAgent.cs`、`ProjectAgentTools.cs` | 範例 Agent 與兩個範例工具（0.9.142 起）|
| `MyProject.Web/Configuration/AiModelSettings.cs` | 各功能的模型、`Resolve`、`OverriddenModels`（0.9.142 起）|
| `MyProject.Web/Components/Views/AiAssistants/AgentPlaygroundView.razor(.cs/.css)` | 「Agent 測試台」頁（`/agent-playground`，0.9.142 起）|
| `MyProject.Web/Ai/AiChatEndpoint.cs` | 兩家供應商的 base URI 與設定驗證 |
| `MyProject.Web/Ai/AiChatCompletionClient.cs` | 設定檢查 → 建 agent → 執行 → 轉回 `AiAnalysisResult`；`StreamAsync` 轉成 `AiStreamUpdate` 序列（0.9.141 起）|
| `MyProject.Web/Ai/AiHealthProbe.cs` | 健康探測（30 秒逾時、不檢查用量上限）|
| `MyProject.Tests/AiAgentPipelineTests.cs` | SDK 帶來的風險：不重試、Azure 不送 Authorization、自己的逾時、SDK 產生的 body 仍可解析、探測不受上限影響 |
| `MyProject.Tests/AiTestAgents.cs` | 測試用工廠：沿用舊建構子的參數順序，既有測試只換建立方式、斷言不改 |
| `MyProject.Tests/AiStreamingTests.cs`、`AiChatStreamParserTests.cs`、`AiInterruptionTests.cs`、`AiSse.cs` | 串流記帳（成功、送出 `stream` 與 `include_usage`、取消前／後、上游錯誤、空回應、長度截斷、中途卡住逾時、未設定）、SSE 解析、已中止註記；`AiSse` 組測試用 SSE（0.9.141 起）|
| `MyProject.Tests/Components/ExceptionAiAnalysisModalStreamingTests.cs` | bUnit：逐段顯示與「停止產生」、停止後保留並標示已中止、未收到文字就停止（0.9.141 起）|
| `MyProject.Tests/AiAgentRunnerTests.cs` | 工具呼叫迴圈每次模型呼叫各記一筆且 RunId 相同、沒權限不執行、異動工具核准後才執行、拒絕與沒權限的稽核、等待核准時不能送訊息、工具有送給模型（0.9.142 起）|
| `MyProject.Tests/AgentToolsAndModelTests.cs` | 範例工具的團隊範圍、`AiModelSettings` 解析與覆寫清單、日誌分析以覆寫的模型送出並記帳（0.9.142 起）|

## 9. 相關文件

- [開發慣例與限制速查 §6.7](開發慣例與限制速查.md)：LLM 用量記錄的擴充點與紅線
- [架構總覽](架構總覽.md)、[AI 日誌分析](../features/AI日誌分析.md)、[AI 例外分析](../features/AI例外分析.md)
- [Token 用量 PRD](../prd/Token用量-prd.md)、[AI 對話紀錄 PRD](../prd/AI對話紀錄-prd.md)、[AI 用量配額 PRD](../prd/AI用量配額-prd.md)
- [2026-11-02 AI 呼叫改走 MAF](../changelog/2026-11-02-AI呼叫改走MAF.md)、[2026-11-02 AI 串流與停止產生](../changelog/2026-11-02-AI串流與停止產生.md)、[2026-11-03 Agent 骨架與測試台](../changelog/2026-11-03-Agent骨架與測試台.md)
