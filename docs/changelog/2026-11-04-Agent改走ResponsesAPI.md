# Agent 功能改走 Responses API（0.9.149）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.149
- 首次實作版本：0.9.149
- 最後核對日期：2026/11/04

## 問題

「Agent 測試台」與「Agent 工作流程」使用 Azure 部署 `gpt-6.1-sol` 時，第一次模型呼叫就失敗。
0.9.146～0.9.148 嘗試用推論等級設定解決（見 [Agent 工具推論等級設定](2026-11-04-Agent工具推論等級設定.md)），實測證明在 Chat Completions 上沒有任何設法行得通：

| 送出的 `reasoning_effort` | Azure `gpt-6.1-sol` 的回應 |
|---|---|
| 不送 | 400：Function tools with reasoning_effort are not supported for gpt-6.1-sol in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'. |
| `none` | 400 `unsupported_value`：does not support 'none' with this model. Supported values are: 'low', 'medium', 'high', and 'xhigh'. |
| low～xhigh | 仍是工具搭配推論，同第一列 |

2026/11/04 以同一個部署實測 `/v1/responses`（`store=false`、帶 function tool）：非串流與串流、第一輪要求呼叫工具、第二輪送回工具結果後回答，全部 200。

## 修正

### 一、Agent 功能改走 Responses API

- `AiCallContext` 新增 `UseResponsesApi`，只有 `AiAgentRunner`（Agent 測試台）與 `AiWorkflowRunner`（Agent 工作流程）設為 true。
  流程裡不帶工具的步驟（撰寫者、審查者、彙整者）也走 Responses，同一個流程不混用兩種 API。
- `AiAgentFactory` 依旗標選 `OpenAIClient.GetResponsesClient().AsAIAgent(...)` 或原本的 `GetChatClient(model).AsAIAgent(...)`；兩條路都經過同一個記帳中介層 `AiAccountingChatClient`（用量上限、逾時、錯誤對應、Token 用量、AI 對話紀錄不變）。
- **一律 `store=false`**，並帶 `include: reasoning.encrypted_content`：對話不留在雲端；歷史仍由 MAF session 每次整段重送，不使用 `previous_response_id`。Agent 對話的保存與接續（含停在核准時接續）照常運作。
- AI 日誌分析、AI 例外分析、健康探測**維持 Chat Completions**，行為不變。

### 二、記帳與對話紀錄看得懂 Responses 格式

不改的話，每一次最終回答都會被判成「空回應」而失敗、用量全空。

- `AiChatResponseParser` 依內容自動判斷格式（根物件有 `output` 陣列／串流 data 帶 `type: response.*`）：
  - 文字：`output` 裡 message 的 `output_text` 串接；串流以 `response.completed` 帶的完整 response 為準，中途取消時由已收到的 `response.output_text.delta` 串起。
  - 用量：`input_tokens`、`output_tokens`、`total_tokens`、`input_tokens_details.cached_tokens`（快取）、`output_tokens_details.reasoning_tokens`（推理）。Token 用量頁的 `RawUsageJson` 存 Responses 的 usage 原文。
  - 結束原因：`completed`→`stop`；`incomplete`＋`max_output_tokens`→`length`（空回應訊息照樣指向 MaxOutputTokens）；Azure `content_filters[]` 被擋時取第一個被過濾的類別。
- `AiCallLogMessages.Parse`（AI 對話紀錄頁與 PDF）讀 Responses 請求：`instructions`→系統提示、字串 `input`→使用者、message 項目照原角色（`developer` 視為系統提示）、`function_call`→AI 助理「呼叫工具 名稱：參數」、`function_call_output`→工具結果，推論項目略過。
- 角色 `tool` 顯示為「工具結果（Tool）」（原本直接顯示英文 `tool`）。

### 三、推論等級設定改名：`ToolReasoningEffort` → `AgentReasoningEffort`

- 意義改為「Agent 功能（Responses）送出的 `reasoning.effort`」，不再限於帶工具的呼叫；預設 `null`（不送，由模型決定），可填 `none`／`low`／`medium`／`high`／`xhigh`。
- Chat Completions（分析功能）**永遠不送**推論等級，回到 0.9.145 的行為。
- 400 `param=reasoning.effort`（或 `reasoning_effort`）的訊息改為單一句：「此模型不接受 AiSettings:AgentReasoningEffort 的設定值，請改為 null 或此模型接受的值。」0.9.148「換模型」那一支已不需要。
- 0.9.146～0.9.148 未發布（尚未 commit），改名沒有相容性負擔；舊名稱不保留。

### 四、`OPENAI001` 豁免

OpenAI .NET SDK 2.14 把 Responses API 標為評估用（`[Experimental("OPENAI001")]`），專案的 `TreatWarningsAsErrors` 會讓建置失敗。
只在 `MyProject.Web/Ai/AiAgentFactory.cs` 以 `#pragma warning disable OPENAI001` 豁免，並註明原因與解除條件（SDK 拿掉標記後移除），見 [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md)。

## 實測（2026/11/04，本機 7144、Azure `gpt-6.1-sol`）

| 項目 | 結果 |
|---|---|
| Agent 測試台：「列出所有專案」 | 呼叫「搜尋專案」工具後列出 10 筆，兩次模型呼叫都記到用量 |
| 追問＋修改描述 | 記得前文；修改工具跳出核准窗，核准後完成更新 |
| 重新整理後從「我的對話」接續 | 正確回答剛才修改的是 Id 4；新對話則回答「無法確認」（沒有前文） |
| Agent 工作流程「專案狀態報告」 | 蒐集（兩個工具）→ 撰寫 → 退回附意見 → 重寫 → 通過 → 收尾核准附加摘要，全程成功 |
| Agent 工作流程「多觀點審查」 | 三位審查者並行、彙整成功 |
| AI 對話紀錄頁 | 端點為 `/openai/v1/responses`、結束原因 stop、用量與費用正確；完整對話顯示系統提示、使用者、工具呼叫、工具結果與 AI 回覆 |
| Token 用量頁 | Agent 作業有輸入／輸出／快取／推理數字與費用 |
| 回歸：AI 日誌分析 | 仍走 Chat Completions，串流產生正常 |

## 已知、未處理

頁面關閉（Blazor circuit 釋放）時，日誌會出現多筆 `Unobserved task exception`（`JSDisconnectedException`，堆疊指向 AntDesign Select／DatePicker 的 Dispose）。
2026/10/16 的日誌已有同樣紀錄，與本次改動無關，另案處理。

## 測試

- 新增 `AiResponsesFormatTests`（解析器與對話紀錄的 Responses 格式）。
- `AiSse` 新增 `Responses`／`ResponsesToolCall`（形狀照實測事件）；`AiAgentRunnerTests`、`AiWorkflowRunnerTests` 改用 Responses 假資料，並斷言送到 `/responses`、`store:false`、用量四項、接續時整段歷史重送且不含 `previous_response_id`。
- `AiAgentPipelineTests.AgentReasoningEffort_ShouldOnlyBeSent_ByResponsesAgents`、`AiLogAnalysisServiceTests`（推論等級錯誤訊息）、`AiCallLogPdfBuilderTests`（工具結果標籤）、`OptionsValidationTests`、`AiSettingsTests` 同步更新。
- 測試總數 1907。

## 異動檔案

- `Ai/AiCallContext.cs` — `UseResponsesApi`
- `Ai/AiAgentFactory.cs` — 依旗標選 Responses／Chat Completions、`store=false`、推論等級只給 Responses、`OPENAI001` 豁免
- `Ai/Agents/AiAgentRunner.cs`、`Ai/Workflows/AiWorkflowRunner.cs` — 設 `UseResponsesApi = true`
- `Ai/AiChatResponseParser.cs` — Responses 非串流與串流解析
- `Ai/AiAccountingChatClient.cs` — 推論等級錯誤訊息
- `Ai/AiCallLogPdfBuilder.cs` — `tool` 角色標籤
- `MyProject.Models/Systems/AiCallLogModels.cs` — Responses 請求解析
- `Configuration/AiSettings.cs`、`Configuration/Validation/AiSettingsValidator.cs` — 改名 `AgentReasoningEffort`
- `appsettings.json` — `SystemVersion` 0.9.148 → 0.9.149
- 文件：MAF AI 呼叫架構 §3、日誌與設定檔說明 §4.8、腳手架開發指引、AI 日誌分析、測試指南、CI-CD 與品質檢查、changelog 與文件索引
