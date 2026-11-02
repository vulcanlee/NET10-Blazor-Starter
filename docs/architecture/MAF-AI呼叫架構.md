# MAF AI 呼叫架構

- 文件版本：1.0
- 文件狀態：第一階段已實作（第二～四階段規劃中）
- 現行系統版本：0.9.140
- 首次實作版本：0.9.140
- 最後核對日期：2026/11/02

> 系統內**所有** LLM 呼叫都經由 Microsoft Agent Framework（MAF）建立的 agent 送出（0.9.140 起）。
> 本文件說明管線怎麼組、記帳點在哪裡、為什麼這樣決定，以及後續三個階段的範圍。
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
呼叫端（AiChatCompletionClient／AiHealthProbe／未來的 Agent 功能）
  │  IAiAgentFactory.CreateAgent(AiCallContext)          ← 唯一入口
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
- **串流**：第一階段記帳中介層**明確拒絕**串流呼叫（`NotSupportedException`），絕不讓串流繞過記帳；第二階段接上。

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

## 5. 新增 LLM 呼叫點

- 「使用者按按鈕、送一段對話」：照舊用 `IAiChatCompletionClient`，只給 `Operation` 與三段情境化提示文字。
- 其他情境：注入 `IAiAgentFactory`，`CreateAgent(new AiCallContext { Operation, Timeout, … })` 後用 MAF 的
  `RunAsync` 執行，`catch (AiCallException)` 轉成自己的結果。記帳、對話紀錄、用量上限都自動套用。
- ⚠️ **不要**自己 `new OpenAIClient`、`AsIChatClient()` 或直接用 `HttpClient` 打 API —— 那等於繞過記帳（§6.7）。
- 作業名稱仍要登記在 `TokenUsageOperations`，模型費率仍要補在 `AiPricingSettings.Models`。

## 6. 後續階段

| 階段 | 範圍 | 狀態 |
|---|---|---|
| 一 | 換底層，行為不變（本文件）| 0.9.140 已實作 |
| 二 | 串流：記帳中介層支援串流（串流結束取用量；已收到內容才取消 → 記一筆「已取消」、用量空白）；AI 日誌分析與 AI 例外分析改為逐段顯示，加「停止產生」，停止後保留已產生內容並標示已中止 | 規劃中 |
| 三 | Agent 骨架：C# 定義 agent、每個 agent 可指定模型；工具呼叫逐次記帳並以 RunId 串起（`AiCallLog`／`TokenUsageLog` 加欄位）；工具依使用者 RBAC 執行、會改資料的工具強制核准；多輪對話存檔案系統、SQLite 只放中繼資料（本人＋管理員可見、保存天數可設定）| 規劃中 |
| 四 | 多 Agent 工作流程（MAF Workflows）：所有 agent 經 `IAiAgentFactory` 建立、共用一個 RunId；人工介入當下回應，不做跨重開機檢查點 | 規劃中 |

共同原則：非結構、會佔大空間的資料（對話內容、工具參數與結果）一律存檔案系統，資料庫只放可查詢的中繼資料；
工具參數與結果只進 AI 對話紀錄檔，稽核只記工具名稱、使用者、成敗與是否被擋。

## 7. 程式位置

| 檔案 | 職責 |
|---|---|
| `MyProject.Web/Ai/AiAgentFactory.cs` | `IAiAgentFactory`：組 OpenAI SDK（端點、重試、逾時、傳輸、管線原則）並以 `AsAIAgent(..., clientFactory:)` 掛上記帳中介層 |
| `MyProject.Web/Ai/AiAccountingChatClient.cs` | 記帳中介層：用量上限、逾時、錯誤對應、Token 用量、AI 對話紀錄 |
| `MyProject.Web/Ai/AiPipelinePolicies.cs` | `AiHttpCapturePolicy`（擷取原文）、`AzureApiKeyPolicy`（Azure 認證標頭）|
| `MyProject.Web/Ai/AiCallContext.cs` | `AiCallContext`、`AiCallException`、`AiCallState`、`AiCallOutcome` |
| `MyProject.Web/Ai/AiChatEndpoint.cs` | 兩家供應商的 base URI 與設定驗證 |
| `MyProject.Web/Ai/AiChatCompletionClient.cs` | 設定檢查 → 建 agent → 執行 → 轉回 `AiAnalysisResult` |
| `MyProject.Web/Ai/AiHealthProbe.cs` | 健康探測（30 秒逾時、不檢查用量上限）|
| `MyProject.Tests/AiAgentPipelineTests.cs` | SDK 帶來的風險：不重試、Azure 不送 Authorization、自己的逾時、SDK 產生的 body 仍可解析、探測不受上限影響 |
| `MyProject.Tests/AiTestAgents.cs` | 測試用工廠：沿用舊建構子的參數順序，既有測試只換建立方式、斷言不改 |

## 8. 相關文件

- [開發慣例與限制速查 §6.7](開發慣例與限制速查.md)：LLM 用量記錄的擴充點與紅線
- [架構總覽](架構總覽.md)、[AI 日誌分析](../features/AI日誌分析.md)、[AI 例外分析](../features/AI例外分析.md)
- [Token 用量 PRD](../prd/Token用量-prd.md)、[AI 對話紀錄 PRD](../prd/AI對話紀錄-prd.md)、[AI 用量配額 PRD](../prd/AI用量配額-prd.md)
- [2026-11-02 AI 呼叫改走 MAF](../changelog/2026-11-02-AI呼叫改走MAF.md)
