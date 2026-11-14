# AI Agent PRD（Agent 測試台與 Agent 工作流程）

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：0.10.18
- 首次實作版本：0.9.142
- 最後核對日期：2026/11/14

## 一、目標與範圍

本系統以 Microsoft Agent Framework（MAF）提供「會呼叫工具的 AI Agent」腳手架能力：Agent 的定義方式、工具的權限與人工核准、
多 Agent 工作流程與人工介入、對話保存，以及與既有 AI 記帳（Token 用量、AI 對話紀錄、用量上限）的串接。
兩個頁面「Agent 測試台」與「Agent 工作流程」是這套骨架的**範例與驗收入口**，衍生專案照這些範例新增自己的 Agent。

| 包含 | 不包含 |
|---|---|
| 單一 Agent 對話：逐段顯示、工具呼叫、異動工具需核准、停止產生、對話保存與接續 | RAG／向量檢索、檔案或網頁知識庫 |
| 多 Agent 工作流程：循序＋審閱關卡、並行＋收齊、三種人工介入、執行紀錄 | 通用對話機器人（Agent 只能用定義好的工具） |
| 每一次模型呼叫照常記帳、記對話紀錄、檢查用量上限 | 對話存在雲端（一律 `store=false`，見 §5.2） |
| 各功能分設模型與推論等級（系統參數頁，推論等級 0.9.168 起）；Agent 測試台可依對話選推論等級（0.9.170 起）| 系統管理助理與工作流程在畫面上切換推論等級（見 §5.3、§十） |
| 範例工具：搜尋專案、讀取專案明細、修改描述、附加狀態摘要 | 工作流程從中途接續（沒有跨重開機的檢查點） |

設計細節見 [MAF AI 呼叫架構 §3、§6](../architecture/MAF-AI呼叫架構.md)。
建立在這套骨架上的管理員專屬應用見 [系統管理助理 PRD](系統管理助理-prd.md)（0.9.155 起，每輪自動分派領域的 Agent）。

## 二、使用者與入口

| 頁面 | 路由 | 選單 | 權限鍵 |
|---|---|---|---|
| Agent 測試台 | `/agent-playground` | 「AI 助理」（id 9）›「Agent 測試台」（id 91）| `MagicObjectHelper.角色_AI助理`、`角色_Agent測試台` |
| Agent 工作流程 | `/agent-workflows` | 「AI 助理」›「Agent 工作流程」（id 92，圖示 `account_tree`）| `角色_AI助理`、`角色_Agent工作流程` |

- 兩個頁面都**可授權給角色**，不是管理員專屬。
- 兩個頁面都列入 `RolePermissionService.NotGrantedToDefaultRole`：建立預設角色或升級加入新頁面時**不自動開給預設角色**，因為會花 AI 費用，由管理員決定開給哪些角色。
- Agent 呼叫的工具另外要有對應功能的權限（例如「專案項目」的檢視或編輯），見 §七。
- 操作說明：`Datas/Help/agent-playground.md`、`Datas/Help/agent-workflows.md`。
- **WebAssembly（0.10.7 起，WASM 遷移 M8）**：兩頁改在瀏覽器執行（`MyProject.Web.Client/Pages/AgentPlaygroundPage.razor`、`AgentWorkflowsPage.razor`、`Views/AiAssistants/*`；Server 版已刪除），畫面與訊息和之前相同；資料經 `api/v1/agent-playground`（`[HasPermission(角色_Agent測試台, View)]`，測試專案另要專案項目的新增／刪除）與 `api/v1/agent-workflows`（`[HasPermission(角色_Agent工作流程)]`），對話與執行以 NDJSON 串流（閒置 15 秒送一筆心跳）。端點見《[Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)》2.20、2.21。

## 三、Agent 測試台（0.9.142 起）

### 3.1 範例 Agent 與工具

範例 Agent「專案小幫手（範例）」（`AgentPlaygroundAgent`）：

| 工具 | 名稱 | 所需權限 | 異動 | 說明 |
|---|---|---|---|---|
| `search_projects` | 搜尋專案 | 專案項目：檢視 | 否 | 最多 10 筆，描述截斷 200 字 |
| `update_project_description` | 修改專案描述 | 專案項目：編輯 | **是，需核准** | 經 `ProjectService` 更新，團隊範圍、`Project.Update` 稽核與並行版本號照常生效 |

### 3.2 畫面與操作

- 逐段顯示回答；思考中與產生中顯示秒數；「停止產生」會保留已收到的內容並標示不完整。
- 「新對話」開一段新對話；畫面顯示這段對話使用的模型。輸入最多 2000 字。
- **核准窗**：模型要求呼叫異動工具時跳出，不能按 × 或遮罩關閉，必須核准或拒絕。有待核准的工具時不能送新訊息。
- 右上角「產生 10 筆測試專案」（需要「專案項目」新增權限）與「清除測試專案」（需要刪除權限），0.9.145 起提供。測試專案標題以「【測試】」開頭、不指定團隊，產生後的說明窗附範例問句；已有測試專案時不再產生。

### 3.3 對話保存（0.9.143 起）

- 對話自動保存。左側「我的對話」列出標題、最後對話時間與「待核准」標籤，點一下就能接續。
- 每段對話可「重新命名」（1～100 字）與「刪除這段對話」。刪除不影響 AI 對話紀錄與 Token 用量。
- **只限本人**。管理員要追查，可在「AI 對話紀錄」用同一個 `ConversationId` 看到每一次模型呼叫。
- 自動標題：第一則訊息的前 40 字。
- 接續時用**目前**的模型與提示詞重新建立 Agent，並還原 MAF session（讓 AI 記得前文）、畫面紀錄與待核准。停在核准的對話，接續時會重新跳核准窗。
- 接不下去時（內容檔已清除或格式不相容），畫面顯示紅色提示，使用者可以刪除後開新對話。
- **0.10.7 起（WASM）**：伺服器不把對話留在記憶體，每則訊息與每次核准都「讀存檔 → 接續 → 執行 → 存檔」，所以每則訊息都用**當下**系統參數的模型；推論等級改了，下一則訊息送到伺服器時才套用。網址帶 `?conversation={id}`，重新整理回到同一段對話（停在核准的再跳出核准窗）。
- **併發（0.10.7 起）**：同一段對話一把鎖，兩個分頁同時送出時後到的**排隊**、兩則都完成（Server 版沒有鎖，可能互相覆蓋）；核准以資料庫的條件式更新搶占，同一個核准送兩次只執行一次。Server 版只在畫面擋的規則也搬到伺服器：訊息 2,000 字（400）、等待核准時送新訊息 409、核准編號不在目前待核准裡 409。
- 與 Server 版不同的小地方（0.10.7 起）：按「停止產生」後畫面自己補「已停止產生…」那則；還在排隊就按停止則改為重讀伺服器上的對話；API 錯誤以一般錯誤通知顯示（訊息照抄伺服器）。

## 四、Agent 工作流程（0.9.144 起）

### 4.1 兩條範例流程

| 工作流程 | 步驟 | 示範 |
|---|---|---|
| 專案狀態報告 | **蒐集**（`search_projects`、`get_project_detail`、`ask_user`）→ **撰寫**（Markdown 草稿）→ **審閱關卡** → **收尾**（`search_projects`、`append_project_status_summary`）| 循序流程、三種人工介入 |
| 多觀點審查 | 使用者貼上文件 → **需求完整性**、**技術可行性與風險**、**使用者體驗**三位審查者同時審查 → **彙整**成「彙整意見」| 並行分支與收齊 |

- 審閱關卡：可以通過、退回或結束（不定稿）。退回必須寫意見，撰寫者依意見重寫；最多退回 3 次，之後只剩通過與結束。通過時草稿成為「定稿報告」。
- 範例工具 `get_project_detail`（專案項目：檢視，唯讀）回傳日期、狀態、優先等級、完成度、負責人、分類、團隊、描述（截斷到 1000 字）與附件檔名。
- 範例工具 `append_project_status_summary`（專案項目：編輯，**異動、需核准**）在原描述最後接上「【狀態摘要 yyyy/MM/dd】…」，原描述保留。
- 每個 agent 各有提示詞，「AI 提示詞」頁另有 7 個分頁可以修改。

### 4.2 人工介入

三種人工介入都以頁面上的卡片當下回覆，不是彈出視窗：

| 種類 | 選項 |
|---|---|
| 工具核准 | 拒絕／核准（規則同 Agent 測試台，共用 `AiToolApprovals`）|
| Agent 提問（`ask_user`）| 送出回答（不可空白）|
| 審閱 | 結束（不定稿）／退回／通過，並顯示還能退回幾次 |

- 「停止」會取消整個工作流程。執行結果分為完成、結束（審閱時選結束）、失敗、已取消、中斷五種。
- 開始執行的輸入最多 20000 字；回答 Agent 提問與退回意見各最多 2000 字。

### 4.3 執行紀錄

- 每次執行都存進左側「我的執行紀錄」（依工作流程分開、只限本人），可以唯讀回顧、重新命名、刪除。
- 與 Agent 對話**共用儲存**：資料表 `AiAgentConversation`、內容檔目錄與保留天數都相同，見 §6.2。
- **不能從中途接續**：執行中或等待使用者時離開或重新整理頁面，紀錄會存成「執行中斷（離開頁面），工作流程無法從中途接續。」。等待使用者時切換工作流程、開新的執行或點開別筆紀錄，會先確認是否放棄目前的執行。
- **等待使用者時重新整理可接回（0.10.7 起，WASM）**：執行物件放在伺服器的執行登錄表（`AiWorkflowRunRegistry`），停在等使用者回覆時重新整理或關分頁，執行留在伺服器最多 **30 分鐘**，回到頁面自動接回（`GET api/v1/agent-workflows/runs/active`）；超過 30 分鐘或網站重新啟動就結束成「中斷」。AI 正在輸出時斷線仍中斷整個流程（同 Server 版）。在站內切到別的頁面時，只放棄這個分頁開始或回覆過的執行，另一個請求正在用時不放棄。
- **一人同時只能有一次執行（0.10.7 起）**：第二個開始回 409「你還有一次工作流程在執行或等你回覆（可能在其他分頁），請先完成或放棄它。」；別人的執行一律 404；重複回覆或正在處理 409（畫面重讀狀態）；回答空白、退回沒寫意見、退回次數用完 400。

## 五、模型、API 與推論等級

### 5.1 模型

各功能可在「系統參數」頁的「AI 模型」分類分別指定模型（`AiModelSettings`）。空白時使用 `AiSettings:Model`；端點與金鑰共用 `AiSettings`。

| 系統參數 | 設定鍵 | 生效時機 |
|---|---|---|
| Agent 測試台使用的模型 | `AiModelSettings:AgentPlayground` | 下一段新對話，或從「我的對話」接續時；畫面上開著的對話沿用開始時的模型 |
| 「專案狀態報告」工作流程使用的模型 | `AiModelSettings:ProjectStatusReport` | 下一次執行起；流程內每個 agent 都用它 |
| 「多觀點審查」工作流程使用的模型 | `AiModelSettings:MultiPerspectiveReview` | 下一次執行起 |

模型必須支援 function calling。新模型要在 `AiPricingSettings` 補費率，否則記為未定價。

### 5.2 走 Responses API（0.9.149 起）

- Agent 測試台與 Agent 工作流程的**所有** agent 都走 OpenAI Responses API（`/v1/responses`），包含流程裡不帶工具的步驟；0.9.155 起系統管理助理每一輪的主呼叫（經 `AiAgentRunner`）、0.9.161 起定時巡檢（`AdminPatrolRunner`）也一樣。AI 日誌分析、AI 例外分析、健康探測與系統管理助理的分派判斷仍走 Chat Completions。
- **原因**：Azure 的 `gpt-6.1-sol` 在 Chat Completions 上無法搭配工具。不送推論等級會回 400「Function tools with reasoning_effort are not supported」，送 `none` 會回 400 `unsupported_value`。Responses API 則允許（2026/11/04 實測）。
- **一律 `store=false`**，對話不留在雲端。歷史由 MAF session 每次整段重送，不使用 `previous_response_id`。
- 切換點只有一個：`AiCallContext.UseResponsesApi`，只由 `AiAgentRunner`、`AiWorkflowRunner` 與 `AdminPatrolRunner`（0.9.161）設定。

### 5.3 推論等級（reasoning effort）

| 項目 | 說明 |
|---|---|
| 設定鍵 | 各功能：`AiReasoningSettings:AgentPlayground`／`ProjectStatusReport`／`MultiPerspectiveReview`／`AdminAssistant`（0.9.168 起，系統參數「AI 模型」分類）；空白時沿用全域的 `AiSettings:AgentReasoningEffort`（0.9.146～0.9.148 名為 `ToolReasoningEffort`）|
| 預設值 | `null`：不送 `reasoning.effort`，由模型自己決定。**Azure `gpt-6.1-sol` 實際採用 `medium`**（2026/11/04 實測，回應的 `reasoning.effort` 皆為 `medium`）|
| 可填值 | 空白（只限各功能，表示沿用全域值）、`none`、`low`、`medium`、`high`、`xhigh`（不分大小寫，其他值啟動時驗證失敗；系統參數頁用下拉選單）。實際接受哪些值依模型而定：`gpt-6.1-sol` 只接受 `low`～`xhigh`，填 `none` 會被拒 |
| 怎麼改 | 各功能：「系統參數」頁「AI 模型」分類的「◯◯的推論等級」。全域值：開發環境改 User Secrets，正式環境改 `appsettings.json` 或環境變數，例如 `"AiSettings": { "AgentReasoningEffort": "high" }` |
| 生效時機 | 每次建立 agent 時都讀取目前設定（`IOptionsMonitor`），不必重啟：Agent 測試台在開對話或接續時建立 agent，所以是下一段新對話（或從「我的對話」接續）起；系統管理助理每則訊息都重建，下一則訊息起；工作流程下一次執行起；定時巡檢下一次巡檢起 |
| 適用範圍 | Agent 測試台、兩條工作流程（流程裡每個 agent 共用該流程的值）、系統管理助理（定時巡檢共用，分派判斷除外）各一個值。分析功能與分派判斷（Chat Completions）一律不送推論等級 |
| 優先順序（0.9.170 起）| Agent 測試台畫面上這段對話選的 → 系統參數的功能值 → `AiSettings:AgentReasoningEffort` → 不送 |
| 依對話選擇（0.9.170 起）| 只有 Agent 測試台：對話區上方的「推論等級」下拉（「系統預設（目前：…）」＋五個值）。隨時可改，下一則訊息起生效（`AiAgentChat.SetReasoningEffort` 以同一個 session 重建 agent，前文照樣帶著，模型沿用開對話時的，經 `AiCallContext.ModelOverride`）；回覆中或等待核准時不能改。存進對話內容（`AiAgentConversationContent.ReasoningEffort`，只加不改、SchemaVersion 不變），接續時沿用，存的值不合法時當作系統預設；「新對話」回到系統預設。經 `AiCallContext.ReasoningEffortOverride` 送出 |
| 被拒時 | 400 `param=reasoning.effort`，畫面與日誌指名用到的設定：對話選的是「此模型不接受推論等級「none」（這段對話選的推論等級），請在對話上方改選其他等級或「系統預設」。」；功能自己的值是「此模型不接受推論等級「none」（系統參數「Agent 測試台的推論等級」），請改為空白或此模型接受的值。」；沿用全域值是「此模型不接受推論等級「none」（AiSettings:AgentReasoningEffort 的設定值），請改為 null 或此模型接受的值。」|

推論等級越高，回答通常越周全，但等待時間與推理 Token（費用）也越多。費用依實際用量記在 Token 用量頁，推理 Token 另列。

## 六、記帳、紀錄與資料生命週期

### 6.1 記帳與紀錄

- 每個 agent 都經 `IAiAgentFactory` 建立，**每一次模型呼叫**（含工具呼叫迴圈中的每一輪）各記一筆 Token 用量與 AI 對話紀錄，並在送出前檢查用量上限（見 [AI 用量配額](AI用量配額-prd.md)）。
- `RunId`：Agent 測試台的一則訊息或一次核准回覆是一次執行。工作流程一次執行的所有 agent 共用同一個 `ConversationId` 與 `RunId`，人工介入的停頓前後也不換。
- Token 用量的作業名稱：「Agent 測試台」「專案狀態報告」「多觀點審查」。AI 對話紀錄的關聯說明是「流程／agent」，例如「專案狀態報告／撰寫」。
- Responses 格式的用量（輸入、快取、輸出、推理 Token）與請求內容，記帳與 AI 對話紀錄都能解析，見 [AI 對話紀錄](AI對話紀錄-prd.md) §3.3。
- 提示詞由「AI 提示詞」頁管理，固定安全規則自動附加，見 [AI 提示詞管理](AI提示詞管理-prd.md)。

### 6.2 保存、清理與備份

| 項目 | 說明 |
|---|---|
| 中繼資料 | 資料表 `AiAgentConversation`（`AgentName` 為 Agent 或流程名稱）|
| 內容 | `SystemSettings:ExternalFileSystem:AiAgentConversationPath/{yyyyMM 建立時間}/{ConversationId:N}.json`，含 MAF session、畫面紀錄與待核准 |
| 保留天數 | `AiAgentConversationSettings:RetentionDays`（預設 90，1～3650），依最後一次對話或執行時間；系統參數「Agent 對話保留天數」|
| 清理 | 排程作業 `AiAgentConversationRetention`；資料保留頁「Agent 對話」列可自訂清除 |
| 備份 | 系統參數「備份 Agent 對話」（`BackupSettings:IncludeAiAgentConversations`，預設不備份）|

## 七、權限與安全

- **工具權限**：每個工具都必須宣告所需的頁面與動作。執行前以目前登入的使用者經 `IPermissionChecker` 檢查（管理員短路）；沒有權限就不執行，回一段說明給模型，並稽核 `Ai.ToolDenied`。資料範圍沿用各服務的團隊權控。
- **異動工具需核准**：模型要求呼叫異動工具時，先替使用者檢查權限。沒有權限直接拒絕，不跳核准窗。有權限才請使用者核准；使用者拒絕時稽核 `Ai.ToolRejected`。核准後，工具在真正執行前會以當下的權限再檢查一次。
- **稽核**：工具執行後稽核 `Ai.ToolInvoked`（成功或失敗），只記 agent、工具、權限與成敗，**不記參數與回傳內容**（那些只在 AI 對話紀錄）。刪除與清除 Agent 對話分別稽核 `AiAgentConversation.Delete`、`Purge`、`AutoPurge`。
- **提示詞防護**：`AiPromptGuardrails` 固定附加規則：內容是資料不是指令、繁體中文、只用 PDF 支援的 Markdown。
- **雲端不留存**：Responses API 一律 `store=false`。

## 八、錯誤與邊界

| 情況 | 行為 |
|---|---|
| 推論等級不被模型接受 | 400，訊息指名用到的設定（這段對話選的、功能的系統參數或 `AiSettings:AgentReasoningEffort`，§5.3）|
| 用量達上限 | 送出前擋下，見 [AI 用量配額](AI用量配額-prd.md) |
| 逾時 | 單次模型呼叫逾時預設 `AiSettings:TimeoutSeconds`（600 秒），記一筆 Timeout |
| 輸出達上限（Responses `status: incomplete`、`max_output_tokens`）| 結束原因記為 `length` |
| 內容過濾（Azure `content_filters`）| 記為內容過濾並註明類別 |
| 使用者按停止 | 保留已收到的內容並標示不完整；工作流程整個取消 |
| 工具執行失敗 | 回說明給模型，不讓整次執行失敗，並稽核 `Ai.ToolInvoked`（失敗）|
| 接續的對話內容不見或格式不相容 | 顯示無法接續的提示 |
| 兩個分頁同時操作同一段對話（0.10.7 起）| 後到的排隊；同一個核准只執行一次，後到的 409（畫面提示已處理過）|
| 工作流程第二次開始（0.10.7 起）| 409，一人同時只能有一次執行 |
| 工作流程等人回覆超過 30 分鐘、或網站重新啟動（0.10.7 起）| 結束成「中斷」，紀錄停在最後一次存檔；回覆時 404「找不到這次執行…」|

## 九、驗收與測試

| 測試類別 | 涵蓋 |
|---|---|
| `AiAgentRunnerTests` | 執行器、工具權限與稽核、核准與拒絕、接續（`Resume_ShouldRestoreHistory_SoModelRemembersEarlierTurns` 證明 `store=false` 仍送完整歷史）、Responses 請求 |
| `AiWorkflowRunnerTests` | 兩條工作流程、三種人工介入、退回上限、收尾沒權限或被拒、上游失敗、停止、離開頁面存成中斷 |
| `AgentToolsAndModelTests` | 範例工具的團隊範圍與內容處理、各功能模型的解析 |
| `AiAgentPipelineTests` | SDK 管線、送到 `/responses` 並帶 `store:false`、推論等級只對 Responses 送、功能的值蓋過全域值、被拒時訊息指名用到的設定 |
| `AiReasoningSettingsTests` | 各功能推論等級的解析與驗證；每個走 Responses 的 Agent 都有自己的推論等級設定，也都在系統參數頁 |
| `AiResponsesFormatTests` | Responses 非串流與串流的內容、用量、結束原因解析 |
| `AiAgentConversationServiceTests` | 對話保存、只限本人、改名、刪除與稽核、依最後對話時間清理、內容檔路徑防護 |
| `AgentConversationApiTests`、`AgentWorkflowApiTests`（0.10.7 起）| Web API：串流、接續、核准只執行一次、同一段對話排隊、權限 403；工作流程重新整理後接回、一人一次、別人看不到、閒置逾時、停止、斷線中斷、伺服器端規則 |
| `AiAgentConversationFileStoreTests`、`NdjsonHeartbeatTests`、`Client/AgentDtoMapperTests`（0.10.7 起）| 內容檔共用讀寫、串流心跳、DTO 往返 |

完整清單見 [測試指南](../guides/測試指南.md)。本機實測（2026/11/04，Azure `gpt-6.1-sol`）：Agent 測試台的工具呼叫、核准與接續，以及兩條工作流程都成功；AI 對話紀錄與 Token 用量的數字正確。0.10.7 起另有呼叫真實模型的實機腳本 `scripts/e2e/m08-agents.mjs`（核准與拒絕、核准中重新整理、兩分頁排隊、工作流程人工介入中重新整理接回到完成）。

## 十、已知限制與規劃中需求

**已知限制**

- 推論等級依功能設定（0.9.168 起）；只有 Agent 測試台能在畫面上依對話切換（0.9.170 起），系統管理助理與工作流程不行；工作流程裡的 agent 共用該流程的值，不能逐一分設。
- 工作流程不能從中途接續（0.10.7 起等使用者回覆時重新整理可在 30 分鐘內接回，但這只在同一個行程內：IIS 回收或重啟後就沒了，多台主機也不共用）。
- Agent 對話的「每段對話一把鎖」只在同一個行程內有效（核准另以資料庫條件式更新保證只執行一次）。
- 範例工具只涵蓋專案項目。

**規劃中需求**（尚未提供，不屬於目前的驗收範圍）

- 目前沒有。（0.9.170 已提供 Agent 測試台依對話選擇推論等級。）

## 十一、相關程式與文件

- 程式：`MyProject.Web/Ai/Agents/`（`AiAgentDefinition`、`AiAgentToolFactory`、`AiAgentRunner`、`AiToolApprovals`、`AgentPlaygroundAgent`、`ProjectAgentTools`）、`MyProject.Web/Ai/Workflows/`（`AiWorkflowRunner`、`AiAgentStep`、`ProjectStatusReportWorkflow`、`MultiPerspectiveReviewWorkflow`）、`Ai/AiAgentFactory.cs`、`Ai/AiAccountingChatClient.cs`、`Ai/AiChatResponseParser.cs`、`Configuration/AiSettings.cs`、`Configuration/AiModelSettings.cs`。
- 頁面：0.10.7 起在 `MyProject.Web.Client/Pages/AgentPlaygroundPage.razor`、`AgentWorkflowsPage.razor`，以及 `Views/AiAssistants/` 下的對應檢視（之前在 `MyProject.Web/Components/`）；Web API `Controllers/AgentPlaygroundController.cs`、`AgentConversationEndpoint.cs`、`AgentWorkflowsController.cs`，執行登錄表 `Ai/Workflows/AiWorkflowRunRegistry.cs`，對話鎖 `Ai/Agents/AiAgentConversationLocks.cs`。
- 文件：[MAF AI 呼叫架構](../architecture/MAF-AI呼叫架構.md)、[日誌與設定檔說明](../operations/日誌與設定檔說明.md)、[AI 對話紀錄](AI對話紀錄-prd.md)、[Token 用量](Token用量-prd.md)、[系統參數](系統參數-prd.md)、[AI 提示詞管理](AI提示詞管理-prd.md)。
- changelog：[Agent 骨架與測試台](../changelog/2026-11-03-Agent骨架與測試台.md)、[Agent 對話保存](../changelog/2026-11-03-Agent對話保存.md)、[Agent 工作流程](../changelog/2026-11-04-Agent工作流程.md)、[Agent 測試台的測試專案](../changelog/2026-11-04-Agent測試台測試專案.md)、[Agent 改走 Responses API](../changelog/2026-11-04-Agent改走ResponsesAPI.md)、[WASM 遷移 M8](../changelog/2026-11-11-WASM遷移M8Agent系統管理助理與通知推播.md)。

> 返回 [PRD 主控台](README.md)
