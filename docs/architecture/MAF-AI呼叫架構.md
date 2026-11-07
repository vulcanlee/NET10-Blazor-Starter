# MAF AI 呼叫架構

- 文件版本：1.13
- 文件狀態：第一～四階段已實作
- 現行系統版本：0.9.170
- 首次實作版本：0.9.140
- 最後核對日期：2026/11/07

> 系統內**所有** LLM 呼叫都經由 Microsoft Agent Framework（MAF）建立的 agent 送出（0.9.140 起）。
> 本文件說明管線怎麼組、記帳點在哪裡、為什麼這樣決定、Agent 骨架怎麼用（§6）、對話怎麼保存與接續（§6.7）、多 Agent 工作流程（§6.8），以及各階段的範圍。
> 新增 LLM 呼叫點的紅線仍以 [開發慣例與限制速查 §6.7](開發慣例與限制速查.md) 為準。
> Agent 測試台與 Agent 工作流程的產品面（範圍、權限、模型與推論等級、資料生命週期）見 [AI Agent PRD](../prd/AI-Agent-prd.md)（0.9.151 起）。

---

## 1. 為什麼改用 MAF

0.9.139 以前，AI 日誌分析、AI 例外分析與健康探測都是手刻 HTTP（自己組 JSON、自己 POST `/chat/completions`）。
這對「送一段對話、拿一段回答」夠用，但腳手架與從它複刻出的專案（例如交接知識助理）接下來要做的是
**Agent**：會呼叫工具、會多輪對話、會多個 agent 串成工作流程。這些能力 MAF 都有現成的，手刻 HTTP 沒有。

前提是**現有功能不能壞**，速查表 §6.7 的記帳紅線要完整保留。所以第一階段只換底層，畫面、服務介面、
記帳結果、錯誤訊息都維持原樣，並以既有測試證明。

## 2. 管線

```
呼叫端（AiChatCompletionClient／AiHealthProbe／AiAgentRunner／AiWorkflowRunner／AdminPatrolRunner）
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
| 分析功能（AI 日誌分析、AI 例外分析、健康探測）沿用 **Chat Completions**；**Agent 功能（Agent 測試台、Agent 工作流程）0.9.149 起走 Responses API** | Azure 的 `gpt-6.1-sol` 在 Chat Completions 上**無法帶工具**：不送推論等級回 400「Function tools with reasoning_effort are not supported」，送 `none` 回 400 `unsupported_value`（只接受 low～xhigh），送其他值又是工具搭配推論（2026/11/04 實測）；Responses API 則允許。切換點只有一個：`AiCallContext.UseResponsesApi`（只由 `AiAgentRunner`、`AiWorkflowRunner` 與 `AdminPatrolRunner`（0.9.161 起）設定，流程裡不帶工具的步驟也走 Responses，同一個流程不混用），`AiAgentFactory` 依它選 `GetResponsesClient()` 或 `GetChatClient()`，兩條路經過同一個記帳中介層。⚠️ **一律 `store=false`**（`CreateResponseOptions.StoredOutputEnabled = false`、帶 `reasoning.encrypted_content`）：對話不留在雲端，歷史仍由 MAF session 每次整段重送、不用 `previous_response_id`（`AiAgentRunnerTests.Resume_ShouldRestoreHistory_SoModelRemembersEarlierTurns` 守門）。記帳解析器 `AiChatResponseParser` 依內容自動判斷兩種格式（根物件有 `output` 陣列／串流 data 帶 `type: response.*`），`status: incomplete`＋`max_output_tokens` 對應成 `length`；`AiCallLogMessages.Parse` 讀 `instructions`＋`input`（工具呼叫顯示為 assistant「呼叫工具 名稱：參數」、工具結果為 tool）。`AiSettings:AgentReasoningEffort` 只對 Responses 送 `reasoning.effort`。OpenAI SDK 2.14 把 Responses 標為評估用（`OPENAI001`），只在 `AiAgentFactory.cs` 以 `#pragma` 豁免 |
| Azure 也用 `OpenAI` 官方 SDK（指向 `{Endpoint}/openai/v1`），**不用** `Azure.AI.OpenAI` | 後者新版（2.9.0-beta）仍是預覽；v1 端點用 OpenAI SDK 即可 |
| Azure 一律送 **`api-key` 標頭**（`AzureApiKeyPolicy` 移除 `Authorization`）| SDK 預設送 Bearer，官方沒有明文保證 Azure v1 接受；改送 `api-key` 和 0.9.139 以前完全一致 |
| SDK **重試設 0**（`ClientRetryPolicy(maxRetries: 0)`）| SDK 預設對 408／429／5xx 重試 3 次，每次都可能計費卻只記一筆帳；也會掩蓋設定錯誤 |
| SDK `NetworkTimeout` 與具名 HttpClient 的 `Timeout` 都設**無限**，逾時由記帳中介層 `CancelAfter` 控管 | SDK 預設每次網路操作 100 秒，會砍掉想比較久的推論模型；自己控管才分得出「使用者取消」與「逾時」，健康探測也能有自己的 30 秒 |
| `AiHttpCapturePolicy` 放在 **SDK 管線**而不是 HttpClient 的 DelegatingHandler | 管線是我們在 `AiAgentFactory` 自己組的，正式環境與測試（假的 `HttpMessageHandler`）走同一條路 |
| 每個作業各建一個 agent，`AiCallState` 交接「目前這一次呼叫」的擷取物件 | 同一個 agent 的模型呼叫是循序的，不需要 AsyncLocal |

套件版本集中在 `src/MyProject/Directory.Packages.props`：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.OpenAI` 1.28.0、
`Microsoft.Agents.AI.Workflows` 1.28.0（0.9.144 起，工作流程用）、`Microsoft.Extensions.AI.OpenAI` 10.11.0、`OpenAI` 2.14.0，只有 `MyProject.Web` 參考。
⚠️ `OpenAI` 刻意停在 2.14.0（0.9.164）：2.15.0 改了 `ResponsesClient.CreateResponseStreamingAsync` 的簽章，
`Microsoft.Extensions.AI.OpenAI` 10.11.0 執行時會丟 `MissingMethodException`（編譯不會發現，測試才抓得到）。
要等 `Microsoft.Extensions.AI.OpenAI` 改相依 2.15 以上再一起升。

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
**B** 再做對話保存（§6.7，0.9.143 起）。0.9.142 的對話只存在記憶體，離開或重新整理頁面就沒了。

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

新增設定區段 `AiModelSettings`（`LogAnalysis`、`ExceptionAnalysis`、`AgentPlayground`；0.9.144 起加工作流程用的 `ProjectStatusReport`、`MultiPerspectiveReview`；0.9.155 起加系統管理助理的 `AdminAssistant`、`AdminAssistantRouter`（空白退回 `AdminAssistant`）），**空字串＝`AiSettings.Model`**，可在系統參數頁「AI 模型」修改。
呼叫端在 `AiCallContext.ModelKey`（經 `AiChatCompletionClient` 的走 `AiChatCompletionRequest.ModelKey`）帶 `nameof(AiModelSettings.屬性)`，由 `AiAgentFactory.CreateAgent` 以 `AiModelSettings.Resolve` 解析
（`IOptionsMonitor`，下一次建立 agent 即生效；Agent 測試台在每段對話的第一則訊息建立 agent，所以畫面上進行中的對話沿用原本的模型；0.9.143 起接續舊對話時重新建立 agent，改用**目前**的模型）。
端點與金鑰共用 `AiSettings`，只能換同一個服務裡的模型；不認得的 `ModelKey` 是程式錯誤，丟 `InvalidOperationException`。
「系統健康監控」的「AI 計費表」檢查預設模型與每一個有覆寫的模型（`OverriddenModels()`），任一查無費率即降級。設定說明見 [日誌與設定檔說明 §4.17](../operations/日誌與設定檔說明.md)。

**各功能的推論等級（`AiReasoningSettings`，0.9.168 起）**：鍵名和 `AiModelSettings` 的 Agent 功能同名（`AgentPlayground`、`ProjectStatusReport`、`MultiPerspectiveReview`、`AdminAssistant`），
同樣以 `AiCallContext.ModelKey` 對應；`AiAgentFactory.CreateAgent` 每次讀 `GetOverride(ModelKey)`，空白（或沒有對應的鍵）時用 `AiSettings.AgentReasoningEffort`，仍只在 `UseResponsesApi` 時送 `reasoning.effort`。
工廠把實際送出的值與設定來源（系統參數的名稱或 `AiSettings:AgentReasoningEffort`）交給記帳中介層，模型以 400 `reasoning.effort` 拒絕時，訊息指名要改的那一個設定。
0.9.170 起 `AiCallContext.ReasoningEffortOverride` 優先於上述兩者（來源 `AiReasoningEffortOrigin.Conversation`）：Agent 測試台依對話選的推論等級，`AiAgentRunner.StartAsync(definition, reasoningEffort)` 帶入、`AiAgentChat.SetReasoningEffort` 以同一個 session 重建 agent（以 `AiCallContext.ModelOverride` 沿用開對話時的模型），值存進 `AiAgentConversationContent.ReasoningEffort`，接續時沿用。
不放進 `AiModelSettings`：`OverriddenModels()` 會把每個 string 屬性當成模型名稱拿去查費率。`AiReasoningSettingsTests` 守門：每個走 Responses 的 Agent 都有自己的推論等級設定，也都在系統參數頁。設定說明見 [日誌與設定檔說明 §4.18](../operations/日誌與設定檔說明.md)。

### 6.6 新增一個 Agent

1. `AiAgentDefinition` ＋ 每個工具一個 `AiToolDefinition`（權限必填、異動工具設 `IsMutating`；工具方法呼叫既有服務、回給模型的欄位精簡），工具類別註冊到 DI。
2. `PromptTemplateKeys` 加種類（含 `All`）、`AiPromptGuardrails` 補內建提示詞與「不可信內容」說法、`PromptTemplateView` 補分頁名稱。
3. `TokenUsageOperations` 加作業名稱；新模型補 `AiPricingSettings.Models` 費率。
4. `AiModelSettings` 加屬性 → `SystemParameterCatalog.All` 加一行 → appsettings.json 加鍵 → [日誌與設定檔說明 §4.15](../operations/日誌與設定檔說明.md) 加一列；`AiReasoningSettings` 照樣加同名的推論等級（0.9.168 起）。
5. 頁面用 `IAiAgentRunner`（對話保存內建，「我的對話」清單、改名、刪除用 `AiAgentConversationService`，見 §6.7）；頁面權限照新增頁面的四處同步，會花 AI 費用的頁面加進 `RolePermissionService.NotGrantedToDefaultRole`（不自動開給預設角色），並寫操作說明。

步驟細節見 [腳手架開發指引 §3.11](../guides/腳手架開發指引.md)「新增一個 Agent」。

### 6.7 對話保存與接續（第三階段 B，0.9.143 起）

「Agent 測試台」的對話會自動保存，左側「我的對話」點一下就能接著聊，AI 記得前文。

**存在哪裡**：資料庫只放清單與清理用的中繼資料，內容一律存檔案系統（同 AI 對話紀錄）。

| 位置 | 內容 |
|---|---|
| 資料表 `AiAgentConversation`（migration `AddAiAgentConversation`）| `ConversationId`（唯一，＝AI 對話紀錄的 `ConversationId`）、`UserId`、`Account`、`AgentName`、`Title`、`CreatedAt`、`UpdatedAt`（最後一次對話，排序與過期都依它）、`HasPendingApproval`、`ContentFile` |
| 內容檔 `{AiAgentConversationPath}/{yyyyMM 建立時間}/{ConversationId:N}.json`，每次儲存整份覆寫 | `AiAgentConversationContent`（`SchemaVersion` 1）：`Session`（MAF `SerializeSessionAsync` 的結果，讓 AI 記得前文，含工具呼叫與結果）、`Transcript`（畫面紀錄：User／Assistant／Tool／Notice／Error；0.9.144 起工作流程另有 Output 種類與 `Agent` 欄位，見 §6.8.4）、`PendingApprovals`（等使用者決定的核准請求，或因工具已不存在而自動拒絕的回覆，`AIContent` JSON）|

- **何時存**：`AiAgentChat` 自己維護 `Transcript`，每次執行結束都在 `finally` 存一次（用 `CancellationToken.None`）—— 成功、失敗、使用者停止、停下來等核准，以及呼叫端中途不再讀取（例如離開頁面）；已收到的文字收進紀錄並以 `AiInterruption` 標示不完整。第一則訊息之前不存（只按「新對話」不會多出空白對話）。自動標題是第一則訊息（空白收成一格）的前 40 字＋「…」，只在第一次儲存時寫入。存檔失敗只記警告，不影響對話；寫檔失敗時新的一列 `ContentFile` 為 null。
- **誰看得到**：**只有本人**。`AiAgentConversationService` 的讀、列（依 `UpdatedAt` 新到舊，最多 100 段）、改名（1～100 字、不更新 `UpdatedAt`、不寫稽核）、刪除（先刪列再刪檔，稽核 `AiAgentConversation.Delete`，detail 只有 `agent=…`）都以目前使用者過濾，別人的對話一律當作找不到。規劃時寫「本人＋管理員可見」，實作改為只限本人：管理員要追查仍可在「AI 對話紀錄」以同一個 `ConversationId` 看到每一次模型呼叫。
- **過期**：`AiAgentConversationSettings:RetentionDays`（預設 90，1～3650，沒有不清除；系統參數「資料保留」），排程作業「Agent 對話清理」（`AiAgentConversationRetention`，每天 03:00）刪除 `UpdatedAt` 早於「今天 0 點減 N 天」的對話（全體使用者，先刪列再刪檔），稽核 `AiAgentConversation.AutoPurge`；資料保留頁「Agent 對話」列可立即清理或自訂清除（`AiAgentConversation.Purge`）。⚠️ 月份目錄是建立時間、過期看最後一次對話，所以只逐檔刪、最後收掉空的月份目錄，不整個刪舊月份。
- **接續**：`IAiAgentRunner.ResumeAsync(定義, ConversationId)` 讀回本人的對話 → 重新建立 agent（模型與提示詞用**目前**的設定）→ `DeserializeSessionAsync` → 還原畫面紀錄與待核准。沿用同一個 `ConversationId`，AI 對話紀錄裡前後串成同一段。找不到、不是本人的、不是這個 Agent 的、內容檔不見、`SchemaVersion` 不認得或 session 讀不回來，一律回 null，畫面提示「這段對話無法接續…可以刪除它後開新對話」。
- **停在核准的對話**：接續時重新跳核准窗。權限**不在還原時檢查**，使用者核准後工具外殼（§6.2）在真正執行前以**現在**的權限再檢查一次。存下來的待核准工具若已不在這個 Agent 上，改成自動拒絕，隨下一則訊息一起送回給模型。
- **備份**：內容檔預設不備份（`BackupSettings:IncludeAiAgentConversations`，系統參數「備份 Agent 對話」）；不備份時還原後清單仍在、點開提示無法接續。

### 6.8 工作流程（第四階段，0.9.144 起）

多個 agent 依流程協作，用 MAF Workflows（`Microsoft.Agents.AI.Workflows`）執行，並以「AI 助理 › Agent 工作流程」頁（`/agent-workflows`）驗收。
兩條範例：

| 工作流程 | 形狀 | 步驟 |
|---|---|---|
| 專案狀態報告（`ProjectStatusReport`）| 循序 | **蒐集**（`search_projects`、`get_project_detail`、`ask_user`：找不到或符合多個專案時問使用者）→ **撰寫**（Markdown 報告草稿）→ **審閱關卡**（通過／退回附意見／結束（不定稿），最多退回 3 次，之後只剩通過與結束；通過時草稿成為產出「定稿報告」）→ **收尾**（`search_projects`、`append_project_status_summary`）|
| 多觀點審查（`MultiPerspectiveReview`）| 並行（fan-out）＋收齊 | 使用者貼上文件 → **需求完整性**、**技術可行性與風險**、**使用者體驗**三位審查者同時審查 → **彙整**收齊三份意見後合併成產出「彙整意見」（依審查者固定順序分段，不受誰先完成影響）|

新增的兩個範例工具（`ProjectAgentTools`，同樣經 `ProjectService`，團隊範圍與 `Project.Update` 稽核照常生效）：

| 工具 | 權限 | 異動 | 做什麼 |
|---|---|---|---|
| `get_project_detail`（讀取專案明細）| 專案項目：檢視 | 否 | 回日期、狀態、優先等級、完成度、負責人、分類、團隊、描述（截斷到 1000 字）、附件檔名；看不到的專案回 null |
| `append_project_status_summary`（附加專案狀態摘要）| 專案項目：編輯 | **是** | 在原描述最後面空一行接上「【狀態摘要 yyyy/MM/dd】…」（摘要收成一行、最多 200 字），原本的描述保留 |

#### 6.8.1 為什麼自己寫 agent 步驟

MAF 內建的 agent executor（`AIAgent.BindAsExecutor`）**不用**。實作前以探針驗證過：

- 它把前一個 agent 的**整段對話（含工具呼叫）**轉給下一個 agent，下游看到的不是乾淨的文字；
- 並行分支收齊（fan-in）時，下游 agent 會**跑好幾次**。

所以每個步驟是自訂的 MAF `Executor`（`AiAgentStep`）：自己跑 agent，步驟之間**只傳文字**（`AiWorkflowMessage`，`From` 是送出的 agent 顯示名稱），
收齊幾則才開始由 `AiAgentStepSpec.ExpectedInputs` 決定。探針另外確認了：自訂 executor 的事件即時串流出來、`RequestPort` 來回可用、
`WatchStreamAsync(blockOnPendingRequest: false)` 在停下來等請求時結束串流（狀態 `PendingRequests`）、同一個 superstep 的 executor 會並行執行；
`InProcessExecution.Concurrent` 要求 executor 可跨執行共用，而我們的步驟有狀態，所以用 `InProcessExecution.OffThread`、每次執行重新建立步驟。

#### 6.8.2 人工介入（當下回應）

三種情況都經 MAF 的 `RequestPort`（請求 `AiWorkflowHumanRequest`、回覆 `AiWorkflowHumanResponse`），畫面以卡片直接回覆（不是彈出視窗）：

| 種類 | 誰發出 | 規則 |
|---|---|---|
| 工具核准（`ToolApproval`）| agent 要執行 `IsMutating` 工具 | 同 Agent 測試台（§6.3）：先替使用者檢查權限，沒有權限直接拒絕、稽核 `Ai.ToolDenied`（`stage=approval`）、不跳卡片；使用者拒絕稽核 `Ai.ToolRejected`。規則抽成 `AiToolApprovals`，兩個執行器共用 |
| Agent 提問（`Question`）| 模型呼叫 `ask_user` | `ask_user` **只有宣告、沒有實作**（`AiAgentStep.AskUserDeclaration`，`CanAskUser` 的步驟才有）：工具呼叫迴圈停下來，由步驟轉成提問；回答不可空白，同一個 MAF session 接著跑 |
| 審閱（`Review`）| 審閱關卡 `AiReviewGateStep` | 通過 → 草稿送往下一步並成為產出；退回 → 意見（必填）送回撰寫者（`ComposeRevision`）；結束 → 流程停止（`Ended`）。退回次數到上限後畫面不給退回，執行器也會擋 |

執行器在 `Wire` 之後替每個會等使用者的步驟（agent 步驟與審閱關卡）自動接一個 `RequestPort`（編號 `{步驟}.human`），並把它們登記為產出來源。
「停止」取消整個工作流程（`Canceled`）。

#### 6.8.3 記帳與 RunId

- 每個 agent 仍經 `IAiAgentFactory` 建立，所以每一次模型呼叫照常記 Token 用量與 AI 對話紀錄、照常檢查用量上限。
- **一次執行＝一個 `ConversationId`＋一個 `RunId`**，整個流程的所有 agent 共用，人工介入的停頓前後也不換。
- `Operation`（作業名稱）是流程名稱：`TokenUsageOperations.ProjectStatusReport`「專案狀態報告」、`MultiPerspectiveReview`「多觀點審查」；
  `RelatedInfo`（關聯說明）是「流程／agent」，例如「專案狀態報告／撰寫」，看是哪個 agent 呼叫的。
- 模型：`AiWorkflowDefinition.ModelKey`（`AiModelSettings:ProjectStatusReport`／`MultiPerspectiveReview`），流程裡每個 agent 共用，在開始執行時決定；提示詞也在開始時取一次（含固定附加的安全規則）。

#### 6.8.4 執行紀錄（不能接續）

- 與 Agent 對話（§6.7）**共用儲存**：資料表 `AiAgentConversation`（`AgentName` 是流程名稱，「我的執行紀錄」依它分類）、內容檔目錄 `AiAgentConversationPath`、
  保留天數 `AiAgentConversationSettings:RetentionDays`、排程作業 `AiAgentConversationRetention`、系統參數「備份 Agent 對話」、資料保留頁「Agent 對話」列。只限本人；改名不寫稽核，刪除寫 `AiAgentConversation.Delete`。
- 內容檔只用 `Transcript`：`Session` 為 JSON null、沒有待核准。`AiAgentTranscriptEntry` 多一個可為 null 的 `Agent`（agent、審閱關卡或產出標題的顯示名稱），
  紀錄種類多 `Output`（產出）；只加不改，`SchemaVersion` 仍是 1。
- `AiWorkflowRun` 每一輪讀取（停下來等使用者或結束）都在 `finally` 存檔（`IAiAgentConversationStore`，`CancellationToken.None`）。標題規則同 Agent 對話。
- **不能從中途接續**：沒有跨重開機的檢查點。執行中或等使用者時離開、重新整理頁面（`DisposeAsync`）→ 結果 `Interrupted`，紀錄最後一則
  「執行中斷（離開頁面），工作流程無法從中途接續。」；切換工作流程、開新的執行或點開另一筆紀錄時，若目前的執行還在等使用者，先確認「放棄目前的執行」。
  Agent 測試台的 `ResumeAsync` 也接不起工作流程紀錄（`AgentName` 不同）。
- 結果 `AiWorkflowOutcome`：`Completed`、`Ended`（審閱時選結束）、`Failed`（模型呼叫失敗等，已記帳）、`Canceled`（停止）、`Interrupted`（離開頁面）。

#### 6.8.5 執行器

畫面注入 `IAiWorkflowRunner`：`StartAsync(定義)` 為每個步驟取提示詞、建 agent（`IAiAgentFactory`）與工具（`AiAgentToolFactory`，`CanAskUser` 的步驟加上 `ask_user` 宣告），
呼叫定義的 `Wire` 接邊、自動接上 `RequestPort` 與產出，回傳 `AiWorkflowRun`：

- `RunAsync(輸入)` 開始、`RespondAsync(RequestId, 回覆)` 回覆一筆等待中的請求，各回傳 `IAsyncEnumerable<AiWorkflowUpdate>`，每筆只有一種：
  agent 開始、`TextDelta`、`ToolActivity`、agent 完成、`Request`（停下來等使用者）、`Output`（產出）、`Result`（結束）。
- 以 `InProcessExecution.OffThread` 執行、`WatchStreamAsync(blockOnPendingRequest: false)` 讀取；步驟的即時更新以 `AiWorkflowStepEvent` 送出。
- `Transcript`（畫面直接呈現）、`PendingRequests`、`Result` 由這個物件維護；不是執行緒安全，一次只能有一個讀取。

#### 6.8.6 新增一條工作流程

1. 每個 agent 一個 `AiAgentDefinition`（`Operation`、`ModelKey` 同流程），提示詞種類、`AiPromptGuardrails`、`PromptTemplateView` 分頁名稱照 §6.6 第 2 步。
2. `TokenUsageOperations` 加流程名稱；`AiModelSettings` 加屬性、`SystemParameterCatalog.All`、appsettings.json、[日誌與設定檔說明 §4.17](../operations/日誌與設定檔說明.md) 照 §6.6 第 3、4 步。
3. 寫 `AiWorkflowDefinition`：`Steps`（每個 agent 一個 `AiAgentStepSpec`：`ExpectedInputs`、`ComposeInput`、`ComposeRevision`、`CanAskUser`、`OutputTitle`）
   與 `Wire`（`AiWorkflowGraph` 的 `Step`／`ReviewGate`／`Fork`／`Start`／`Edge`／`FanOut`；人工介入通道與產出由執行器自動接上）。
4. 加進 `AiWorkflowCatalog.All`。頁面是通用的，**不必**另寫畫面；操作說明 `agent-workflows.md` 補上新流程。

步驟細節見 [腳手架開發指引 §3.11](../guides/腳手架開發指引.md)「新增一個工作流程」。

### 6.9 每輪規劃的 Agent（系統管理助理，0.9.155 起）

需求見 [系統管理助理 PRD](../prd/系統管理助理-prd.md)。單一對話入口，每則訊息先判斷領域，再組出這一輪的 agent。

- **`IAiAgentTurnPlanner`**（`Ai/Agents/AiAgentTurn.cs`）：`Home`（含全部工具，開對話與接續用、也是存檔的 AgentName）、`HomeTurnAsync`、`PlanAsync`（每次 `SendAsync` 前呼叫，不丟例外）。
  回傳 `AiAgentTurn(Definition, Instructions, Label)`：這一輪的定義、已組好的系統提示詞（含固定規則）、畫面上的「由誰回答」。
- **`IAiAgentRunner.StartAsync(planner)`／`ResumeAsync(planner, id)`**：`AiAgentChat` 在 `SendAsync` 前依規劃結果重建 agent（`CreateHandle`，同一個 `ConversationId`），
  **MAF session 不換**，前後文延續；`RespondAsync`（核准回覆）沿用提出核准的那一輪，不重新規劃。每輪定義的 `Name` 必須等於 `Home.Name`。
  AI 回覆與工具活動的 `AiAgentTranscriptEntry.Agent` 填這一輪的標籤；沒有規劃器時（Agent 測試台）為 null，行為不變。
- **分派**（`AdminAssistantTurnPlanner`）：用 `IAiChatCompletionClient` 呼叫一次（作業「系統管理助理（分派）」、模型 `AiModelSettings:AdminAssistantRouter`，空白用 `AdminAssistant`），
  模型只回英文代碼 `accounts, monitoring, ai, ops, patrol`（`AdminAssistantRouting.Parse` 整字比對）；失敗或讀不出代碼時退回全部領域。
  0.9.157 起：分派用專用的固定規則（`AiPromptGuardrails.For(AdminAssistantRouter)`：保留 prompt injection 第 1 條、只要求英文代碼、不套 Markdown 規則），
  並以 `AiChatCompletionRequest.Timeout` 限制 20 秒（`AdminAssistantTurnPlanner.RouterTimeout`），逾時同樣退回全部領域。
- **組裝**（`AdminAssistantAgent`）：唯讀工具**每一輪都全部載入**（避免分派漏選就查不到），異動工具只載入被選中領域的；
  提示詞＝共同說明（`AdminAssistant`）＋被選中領域的說明（`AdminAssistantAccounts`／`Monitoring`／`Ai`／`Ops`），以 `IAiSystemPromptProvider.GetInstructionsAsync` 取各段（不含固定規則），最後只 `Compose` 一次。
- **工具**（`Ai/Agents/AdminAssistant/*AssistantTools.cs`，0.9.157 起四類共 26 個：監控 6、維運 7、帳號 7、AI 6）：權限用原頁面的管理員專屬鍵；
  原頁面有寫查詢／檢視稽核的，工具寫同一個稽核代碼並加 `via=admin-assistant`（0.9.155～0.9.156 是 `source=`）；
  參數不合法時不丟例外，夾住範圍或用預設值並在回覆的 `note` 說明（`AdminAssistantToolText.ClampWithNote`、`ParseRange`）。
  最後登入由 `AuditLogQueryService.GetLastLoginTimesAsync` 從稽核推算；使用者輸出絕不含密碼與 Salt，系統參數的孤兒鍵只列鍵名。
- **巡檢**：分派到 `patrol` 時只用唯讀工具，共同說明列出巡檢要呼叫的 9 個工具與七段報告格式（預設最近 24 小時，不呼叫耗時的資料保留總覽）。
- **異動工具**（0.9.158 起，22 個：帳號 10、監控 4、AI 2、維運 6）：`AdminAssistantAgent.MutatingTools[領域]`，`IsMutating = true`（包成 `ApprovalRequiredAIFunction`），
  `AiToolDefinition.ApprovalNote` 顯示在核准窗；`AiToolApprovalRequest` 帶 `Note` 與 `Label`（提出的專家），`Label` 也存進 `AiAgentPendingApprovalRecord`，接續時優先用它還原標籤。
  陣列參數在核准窗列成「共 N 筆」加逐行（最多 50 行），單一值上限 8000 字（`AiToolApprovals.ArgumentMaxLength`，容得下整份提示詞；更長的值工具自己拒絕）。
  2026/11/06 實測：Responses API（`store=false`）歷史中有本輪清單沒有的工具時**仍接受**，所以每輪只給被分派領域的異動工具；換模型或 API 後若回 400，要改成累積工具聯集。
  批次參數用 `string[]`，`AdminAssistantMutation.ValidateBatch`（去重、最多 50）、`CheckAdminsRemain`（整批後至少一位 `IsAdmin && Status`）、`BatchResult`（逐筆結果）。
  不可復原的動作由工具拒絕（縮短保留天數、`OpsAssistantTools.PurgeJobNames` 的清理排程立即執行與啟用）；強制登出自己、取消自己的管理員、改自己的角色也由工具擋（服務層沒有擋）。
  稽核：頁面寫的由工具寫同一個代碼加 `via=admin-assistant`；`MyUserService`、`RoleViewService`、`EmailTestService`、`NotificationTestService` 自己寫的不重複寫。
- **不可復原的工具**（0.9.159 起，15 個）：`AiToolDefinition.IsIrreversible` → `AiToolApprovalRequest.IsIrreversible`（接續時由 `Describe` 從 `Home` 的定義重新帶出），
  核准窗加紅字、紅色核准鈕，`AdminAssistantApproval.CanApprove` 要求輸入「確認」（按鈕停用＋`OnDecideAsync` 再擋一次；輸入框用 `OnInput`，`BindOnInput` 要等失焦）。
  依天數或日期的清除一律走 `custom_purge`（`DataRetentionOverviewService.PurgeCustomAsync`＋`CustomPurgeCutoff`），放在 `AdminAssistantAgent.SharedMutatingTools`，分派到任一專家領域就載入。
  可復原的工具遇到會刪資料的變體（清理排程、縮短保留天數）拒絕並指向不可復原的工具，兩種核准窗不混用。
  分派說明以訊息本身提到的對象為準，前文只補足指稱（實機發現前文會把「徹底刪除 temp-b」帶到 ops）。
- **核准窗的目前狀態**（0.9.160 起）：`AiToolDefinition.ApprovalPreview` 由 `AiAgentToolFactory.PreviewAsync` 在提出核准時算（最多 5 秒、失敗回 null），經 `AiToolApprovals.TriageAsync` 帶到 `AiToolApprovalRequest.Preview`，
  存進 `AiAgentPendingApprovalRecord.Preview`、接續時不重算。工作流程不傳預覽，行為不變。目前掛在修改系統參數的三個工具（`OpsAssistantTools.PreviewParameterChangeAsync`）。
- **定時巡檢**（0.9.161 起，第二版）：排程作業 `AdminPatrolJob`（預設每天 07:30）沒有登入的使用者，所以**不走** `AiAgentRunner`：
  - `AdminPatrolRunner` 直接用 `IAiAgentFactory` 建 agent（作業 `系統管理助理（排程巡檢）`、`ConversationId` 是報告編號、一個 RunId），送一則固定訊息、收齊文字；不存對話、不分派。
  - 定義與提示詞由 `AdminAssistantTurnPlanner.PatrolTurnAsync` 給（`AdminAssistantAgent.ScheduledPatrol`：只有巡檢用的 9 個唯讀工具、同名；提示詞同對話中的巡檢，仍讀「AI 提示詞」的作用中版本）。
  - 權限：一般的 `AiToolPermissionGuard` 在 `CurrentUser.Id == 0` 時拒絕所有工具，所以執行器自己建 `ScheduledPatrolToolGuard`（不註冊 DI），只放行「不會異動且名稱在 `PatrolToolNames`」的工具（巡檢指示列的 9 個，定義上也只給這 9 個）；
    稽核轉給原本的 guard（操作者留空），detail 加「排程巡檢」。有登入使用者的 scope 呼叫 `RunAsync` 會丟例外。出現核准請求（理論上不會）就判失敗。
  - `AdminPatrolReportParser` 從每段第一行「狀態：正常／注意／異常」判讀整體狀態（沒寫狀態算注意、寫「目前無法查詢」至少注意、截斷至少注意、完全判讀不出來是「無法判讀」）；
    報告存 `AdminPatrolReportService`（內文只進內容檔，資料表只存固定用語），需要注意時 `AdminPatrolNotification.Build` 組通知（全體管理員、寄信、摘要＋連結，§6.7 的明文例外）。

## 7. 後續階段

| 階段 | 範圍 | 狀態 |
|---|---|---|
| 一 | 換底層，行為不變（本文件）| 0.9.140 已實作 |
| 二 | 串流：記帳中介層支援串流（串流結束取用量；已收到內容才取消 → 記一筆「已取消」、用量空白）；AI 日誌分析與 AI 例外分析改為逐段顯示，加「停止產生」，停止後保留已產生內容並標示已中止（§2、§4.1）| 0.9.141 已實作 |
| 三 A | Agent 骨架：C# 定義 agent、每個功能可指定模型（`AiModelSettings`）；工具呼叫逐次記帳並以 RunId 串起（`AiCallLog`／`TokenUsageLog` 加欄位）；工具依使用者 RBAC 執行、會改資料的工具強制核准；以「Agent 測試台」頁驗收，對話只在記憶體（§6）| 0.9.142 已實作 |
| 三 B | 對話保存：多輪對話存檔案系統、SQLite 只放中繼資料（只限本人，保存天數可設定、依最後一次對話過期），可接續之前的對話、停在核准的重新跳核准窗（§6.7）| 0.9.143 已實作 |
| 四 | 多 Agent 工作流程（MAF Workflows）：所有 agent 經 `IAiAgentFactory` 建立、共用一個 RunId；人工介入（工具核准、Agent 提問、審閱關卡）當下回應，不做跨重開機檢查點；以「Agent 工作流程」頁的兩條範例驗收（§6.8）| 0.9.144 已實作 |
| 系統管理助理 M1 | 每輪規劃的 Agent：自動分派領域、同一個 session 每輪換工具組與提示詞；監控與診斷、排程的唯讀工具（§6.9）| 0.9.155 已實作 |
| 系統管理助理 M2 | 四大類共 26 個唯讀工具與巡檢報告；分派專用固定規則、分派 20 秒逾時、稽核註記改 `via=`（§6.9）| 0.9.157 已實作 |
| 系統管理助理 M3 | 22 個可復原的異動工具：核准窗說明與批次清單、最多 50 筆、自我與最後一位管理員保護、不可復原動作由工具拒絕（§6.9）| 0.9.158 已實作 |
| 系統管理助理 M4 | 15 個不可復原的異動工具；核准窗紅字警告、輸入「確認」才能核准；自訂清除跨領域共用（§6.9）| 0.9.159 已實作 |
| 系統管理助理第二版 | 定時巡檢：排程作業以「系統排程身分＋窄門」只跑唯讀工具、判讀狀態、存報告、需要注意時通知全體管理員（§6.9）| 0.9.161 已實作 |

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
| `MyProject.Web/Ai/Agents/AiAgentRunner.cs` | `IAiAgentRunner`、`AiAgentChat`（執行、RunId、核准分流；0.9.143 起 `Transcript`、存檔、`ResumeAsync`）、`AiAgentUpdate`、`AiToolActivity`、`AiToolApprovalRequest`（0.9.142 起）|
| `MyProject.Business/Services/DataAccess/AiAgentConversationService.cs` | `IAiAgentConversationStore`（存與讀自己的）、`AiAgentConversationService`（清單、改名、刪除、過期、統計，0.9.143 起）|
| `MyProject.Business/Services/Other/AiAgentConversationFileStore.cs` | Agent 對話內容檔的唯一入口（絕不拋出、擋 `../`、收空月份目錄，0.9.143 起）|
| `MyProject.Models/Systems/AiAgentConversationModels.cs`、`AiAgentConversationSettings.cs`；`MyProject.AccessDatas/Models/AiAgentConversation.cs` | 內容檔結構與清單模型、保留天數設定、資料表（0.9.143 起）|
| `MyProject.Web/Scheduling/Jobs/AiAgentConversationRetentionJob.cs` | 排程作業「Agent 對話清理」（0.9.143 起）|
| `MyProject.Web/Ai/Agents/AgentPlaygroundAgent.cs`、`ProjectAgentTools.cs` | 範例 Agent 與兩個範例工具（0.9.142 起）；0.9.144 起加 `get_project_detail`（`ProjectDetail`）、`append_project_status_summary`，建構子可選 `TimeProvider` |
| `MyProject.Web/Ai/Agents/AiAgentTurn.cs` | `AiAgentTurn`、`IAiAgentTurnPlanner`（每輪規劃，0.9.155 起）|
| `MyProject.Web/Ai/Agents/AdminAssistant/` | 系統管理助理：`AdminAssistantAgent`（定義組裝與內建提示詞）、`AdminAssistantRouting`（領域、分派解析）、`AdminAssistantTurnPlanner`、`AdminAssistantToolText`、`MonitoringAssistantTools`、`OpsAssistantTools`（0.9.155 起）、`AccountsAssistantTools`、`AiAssistantTools`（0.9.157 起）、`AdminAssistantMutation`（0.9.158 起，異動工具的批次與保護）、`AdminAssistantApproval`（0.9.159 起，有不可復原的動作時要輸入「確認」才能核准）|
| `MyProject.Web/Components/Pages/Admins/AdminAssistantPage.razor`、`Components/Views/Admins/AdminAssistantView.razor(.cs/.css)` | 「系統管理助理」頁（`/admin-assistant`，0.9.155 起）|
| `MyProject.Web/Ai/Agents/AdminAssistant/AdminPatrolRunner.cs`、`ScheduledPatrolToolGuard.cs`、`AdminPatrolReportParser.cs` | 定時巡檢（0.9.161 起）：`IAdminPatrolRunner`／`AdminPatrolRunner`（不存對話、不分派）、權限窄門、報告判讀與通知內容（`AdminPatrolNotification`）|
| `MyProject.Business/Services/DataAccess/AdminPatrolReportService.cs`、`Services/Other/AdminPatrolReportFileStore.cs`；`MyProject.AccessDatas/Models/AdminPatrolReport.cs` | 巡檢報告的存放（內容檔在 `AiAgentConversationPath\AdminPatrol`）、清單、讀取、過期與統計（0.9.161 起）|
| `MyProject.Web/Scheduling/Jobs/AdminPatrolJob.cs`、`AdminPatrolReportRetentionJob.cs` | 排程作業「系統管理助理巡檢」與「巡檢報告清理」（0.9.161 起）|
| `MyProject.Web/Components/Views/Admins/AdminPatrolReportsModal.razor(.cs/.css)` | 系統管理助理頁的「巡檢報告」視窗（0.9.161 起，深連結 `?patrolReport=`）|
| `MyProject.Web/Ai/Agents/AiToolApprovals.cs` | 工具核准的共用規則（權限分流、拒絕原因、參數說明），由 `AiAgentChat` 抽出，Agent 測試台與工作流程共用（0.9.144 起）|
| `MyProject.Web/Ai/Workflows/AiWorkflowModels.cs` | 步驟間訊息（`AiWorkflowMessage`／`AiWorkflowRevision`／`AiWorkflowOutput`）、人工介入（`AiWorkflowHumanRequest`／`AiWorkflowHumanResponse`）、`AiWorkflowUpdate`、`AiWorkflowResult`／`AiWorkflowOutcome`、`AiWorkflowStepEvent`（0.9.144 起）|
| `MyProject.Web/Ai/Workflows/AiWorkflowDefinition.cs` | `AiWorkflowDefinition`、`AiAgentStepSpec`、`AiWorkflowGraph`（接線）、`AiWorkflowCatalog.All`（0.9.144 起）|
| `MyProject.Web/Ai/Workflows/AiAgentStep.cs` | `AiAgentStep`（自訂 executor：跑 agent、`ask_user`、工具核准）、`AiReviewGateStep`（審閱關卡）、`AiForwardStep`（並行分支起點）（0.9.144 起）|
| `MyProject.Web/Ai/Workflows/AiWorkflowRunner.cs` | `IAiWorkflowRunner`／`AiWorkflowRunner`、`AiWorkflowRun`（執行、回覆、紀錄、存檔、中斷）（0.9.144 起）|
| `MyProject.Web/Ai/Workflows/ProjectStatusReportWorkflow.cs`、`MultiPerspectiveReviewWorkflow.cs` | 兩條範例工作流程與內建提示詞（0.9.144 起）|
| `MyProject.Web/Components/Pages/AiAssistants/AgentWorkflowsPage.razor`、`Components/Views/AiAssistants/AgentWorkflowsView.razor(.cs/.css)` | 「Agent 工作流程」頁（`/agent-workflows`，0.9.144 起；重新命名窗沿用 `OverlayStyles` 的 `.agent-playground-rename-modal`）|
| `MyProject.Web/Configuration/AiModelSettings.cs` | 各功能的模型、`Resolve`、`OverriddenModels`（0.9.142 起）|
| `MyProject.Web/Configuration/AiReasoningSettings.cs`、`Validation/AiReasoningSettingsValidator.cs` | 各 Agent 功能的推論等級、`GetOverride` 與驗證（0.9.168 起）|
| `MyProject.Web/Components/Views/AiAssistants/AgentPlaygroundView.razor(.cs/.css)` | 「Agent 測試台」頁（`/agent-playground`，0.9.142 起；0.9.143 起左側「我的對話」、重新命名、刪除）|
| `MyProject.Web/Ai/AiChatEndpoint.cs` | 兩家供應商的 base URI 與設定驗證 |
| `MyProject.Web/Ai/AiChatCompletionClient.cs` | 設定檢查 → 建 agent → 執行 → 轉回 `AiAnalysisResult`；`StreamAsync` 轉成 `AiStreamUpdate` 序列（0.9.141 起）|
| `MyProject.Web/Ai/AiHealthProbe.cs` | 健康探測（30 秒逾時、不檢查用量上限）|
| `MyProject.Tests/AiAgentPipelineTests.cs` | SDK 帶來的風險：不重試、Azure 不送 Authorization、自己的逾時、SDK 產生的 body 仍可解析、探測不受上限影響；推論等級只對 Responses 送、功能的值蓋過全域值、被拒訊息指名設定（0.9.168）|
| `MyProject.Tests/AiReasoningSettingsTests.cs` | 推論等級的解析與驗證、每個走 Responses 的 Agent 都有推論等級設定（0.9.168 起）|
| `MyProject.Tests/AiTestAgents.cs` | 測試用工廠：沿用舊建構子的參數順序，既有測試只換建立方式、斷言不改 |
| `MyProject.Tests/AiStreamingTests.cs`、`AiChatStreamParserTests.cs`、`AiInterruptionTests.cs`、`AiSse.cs` | 串流記帳（成功、送出 `stream` 與 `include_usage`、取消前／後、上游錯誤、空回應、長度截斷、中途卡住逾時、未設定）、SSE 解析、已中止註記；`AiSse` 組測試用 SSE（0.9.141 起）|
| `MyProject.Tests/Components/ExceptionAiAnalysisModalStreamingTests.cs` | bUnit：逐段顯示與「停止產生」、停止後保留並標示已中止、未收到文字就停止（0.9.141 起）|
| `MyProject.Tests/AiAgentRunnerTests.cs` | 工具呼叫迴圈每次模型呼叫各記一筆且 RunId 相同、沒權限不執行、異動工具核准後才執行、拒絕與沒權限的稽核、等待核准時不能送訊息、工具有送給模型（0.9.142 起）|
| `MyProject.Tests/AgentToolsAndModelTests.cs` | 範例工具的團隊範圍、`AiModelSettings` 解析與覆寫清單、日誌分析以覆寫的模型送出並記帳（0.9.142 起）|
| `MyProject.Tests/AiAgentConversationServiceTests.cs` | 只限本人、存檔 upsert 與標題、清單排序、改名驗證且不寫稽核、刪除與稽核、依最後對話時間過期、寫檔失敗、路徑穿越（0.9.143 起）；`AiAgentRunnerTests` 另加存檔、接續記得前文、接續停在核准、工具已移除自動拒絕、接不下去回 null、第一則訊息前不存、串流途中取消 |
| `MyProject.Tests/AiWorkflowRunnerTests.cs` | 完整流程（提問、退回、通過、附加摘要、單一 RunId／ConversationId、每個 agent 的關聯說明）、退回 3 次後只剩通過與結束、退回沒寫意見被拒、收尾沒權限自動拒絕、使用者拒絕工具、多觀點審查依固定順序彙整三份意見、上游失敗、停止、等使用者時離開存成中斷且 Agent 測試台接不起來（0.9.144 起）；`AgentToolsAndModelTests` 另加明細的團隊範圍、附加摘要保留原描述／收成一行／截斷、團隊範圍外不修改 |

## 9. 相關文件

- [開發慣例與限制速查 §6.7](開發慣例與限制速查.md)：LLM 用量記錄的擴充點與紅線
- [架構總覽](架構總覽.md)、[AI 日誌分析](../features/AI日誌分析.md)、[AI 例外分析](../features/AI例外分析.md)
- [Token 用量 PRD](../prd/Token用量-prd.md)、[AI 對話紀錄 PRD](../prd/AI對話紀錄-prd.md)、[AI 用量配額 PRD](../prd/AI用量配額-prd.md)
- [2026-11-02 AI 呼叫改走 MAF](../changelog/2026-11-02-AI呼叫改走MAF.md)、[2026-11-02 AI 串流與停止產生](../changelog/2026-11-02-AI串流與停止產生.md)、[2026-11-03 Agent 骨架與測試台](../changelog/2026-11-03-Agent骨架與測試台.md)、[2026-11-03 Agent 對話保存](../changelog/2026-11-03-Agent對話保存.md)、[2026-11-04 Agent 工作流程](../changelog/2026-11-04-Agent工作流程.md)
