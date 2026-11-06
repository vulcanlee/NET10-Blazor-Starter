# 系統管理助理 — 里程碑 0＋1 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在「系統管理 › 管理助理 › 系統管理助理」（`/admin-assistant`，只限管理員）提供一個 Agent 對話入口。每則訊息先自動分派領域，再組出這一輪的 Agent。第一批唯讀工具涵蓋監控與診斷、排程作業。

**Architecture:** 擴充既有的 `AiAgentRunner`／`AiAgentChat`，讓它接受「每輪規劃器」（`IAiAgentTurnPlanner`）：送出訊息前先由規劃器決定這一輪用的定義、指示與專家標籤；同一段對話共用一個 MAF session；核准回覆那一輪沿用原本的定義，不重新分派。系統管理助理的規劃器用一次便宜的 Chat Completions 呼叫做分派。分派只決定要套用哪些領域的說明，以及要不要載入異動工具（M1 還沒有異動工具）；唯讀工具每一輪都全部載入。

**Tech Stack:** .NET 10、Blazor Server、AntDesign、Microsoft Agent Framework 1.27.0（Responses API，`store=false`）、xUnit、SQLite。

**Spec:** `C:\Users\vulca\.claude\plans\ai-groovy-mccarthy.md`（已核准）。Task 0 會把它寫成 `docs/prd/系統管理助理-prd.md`。

## Global Constraints

- 只限管理員：頁面用 `CheckIsAdmin()`；每個工具的 `PermissionPage` 都用原頁面的管理員專屬鍵（`MagicObjectHelper.角色_*`），不放進角色矩陣。
- 所有 LLM 呼叫一律經 `IAiAgentFactory`／`IAiChatCompletionClient`，不可自己 new client。工具一律經 `AiAgentToolFactory` 包裝。
- 唯讀工具要照原頁面寫一樣的查詢／檢視稽核代碼，detail 加上 `source=admin-assistant`。稽核內容不放日誌原文。
- 不讀原始 `IConfiguration`、連線字串或金鑰。
- 分派用的 Token 作業名稱是「系統管理助理（分派）」；對話本身是「系統管理助理」。
- 模型設定鍵為 `AiModelSettings:AdminAssistant`、`AiModelSettings:AdminAssistantRouter`。Router 空白時用 AdminAssistant；AdminAssistant 空白時用 `AiSettings:Model`。
- 對話存在既有的 `AiAgentConversation`，AgentName 為 `AdminAssistant`，只限本人。
- Agent 測試台、Agent 工作流程的行為與既有測試都不能改變。
- `docs/` 與根目錄的 `.md`、`Datas/Help/*.md` 一律 UTF-8 **含 BOM**、繁體中文。
- 整個里程碑結束時，版本號 Patch +1（`0.9.154 → 0.9.155`，格式 `Major.Minor.Patch (YYYY/MM/DD)`），commit message 要說明版本變更。**不自動 commit**，交給使用者決定。
- 提交前在本機跑 `pwsh ./scripts/Invoke-QualityGate.ps1` 並且全部通過。

## Review Focus

1. **分派輸出不合法**（模型回了一段說明文字，或回了不存在的領域）→ 應該退回「套用全部領域的說明」，不能讓整輪失敗。測試在 Task 2、Task 4。
2. **分派呼叫失敗**（逾時、用量上限）→ 這一輪仍要繼續（退回全部領域）。如果主呼叫也因為上限失敗，照現有錯誤顯示。測試在 Task 4。
3. **停在核准時重新整理頁面，再從「我的對話」接續** → 待核准要能還原，核准後照常執行。測試在 Task 1（M1 還沒有異動工具，用測試工具驗證機制）。
4. **工具參數超出範圍或格式錯誤**（take=9999、時間字串打錯、排程名稱不存在）→ 夾住範圍或用預設值，並在回覆中說明實際用了什麼範圍，不丟例外。測試在 Task 5。
5. **非管理員直接打 `/admin-assistant`** → 拒絕存取並寫 `Permission.Denied`，選單看不到。由 Task 6 的守門測試涵蓋。

---

### Task 0: 設計文件入庫

**Files:**
- Create: `docs/prd/系統管理助理-prd.md`
- Modify: `docs/README.md`（文件索引的 PRD 區）

- [ ] **Step 1:** 依核准的設計寫 PRD，格式照 `docs/prd/AI-Agent-prd.md`（文件版本、狀態「實作中」、現行系統版本、目標與範圍表、使用者與入口、架構、工具清單、非目標、里程碑）。內容以設計為準，不要新增設計裡沒有的需求。
- [ ] **Step 2:** 在 `docs/README.md` 的 PRD 索引加一列，指向新文件。
- [ ] **Step 3:** 執行 `pwsh ./scripts/Test-DocsEncoding.ps1`，預期全部通過（含 BOM、沒有亂碼）。

---

### Task 1: 執行器支援「每輪規劃器」

**Files:**
- Create: `src/MyProject/MyProject.Web/Ai/Agents/AiAgentTurn.cs`
- Modify: `src/MyProject/MyProject.Web/Ai/Agents/AiAgentRunner.cs`
- Test: `src/MyProject/MyProject.Tests/AiAgentRunnerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record AiAgentTurn(AiAgentDefinition Definition, string Instructions, string? Label);

  public interface IAiAgentTurnPlanner
  {
      /// 開對話與接續時使用的定義（含全部工具，接續時才找得到待核准的工具）。所有回傳的 Definition.Name 都必須等於 Home.Name。
      AiAgentDefinition Home { get; }
      /// 開對話與接續時的這一輪（不呼叫分派模型）。
      Task<AiAgentTurn> HomeTurnAsync(CancellationToken cancellationToken);
      /// 每次 SendAsync 前呼叫。⚠️ 不丟例外（取消除外）：分派失敗自己退回預設。
      Task<AiAgentTurn> PlanAsync(Guid conversationId, string message, IReadOnlyList<AiAgentTranscriptEntry> transcript, CancellationToken cancellationToken);
  }
  ```
  `IAiAgentRunner` 新增兩個多載：
  `Task<AiAgentChat> StartAsync(IAiAgentTurnPlanner planner, CancellationToken cancellationToken = default)`
  `Task<AiAgentChat?> ResumeAsync(IAiAgentTurnPlanner planner, Guid conversationId, CancellationToken cancellationToken = default)`
  `AiAgentChat` 新增 `string? CurrentLabel { get; }`。

- [ ] **Step 1: 寫失敗的測試**（加在 `AiAgentRunnerTests`，沿用既有的 `upstream`、`handler`、`store`、`guard`、`SampleTools`；另加一個 `ScriptedPlanner` 替身，依序回傳預先排好的 `AiAgentTurn`，並記錄 `PlanCalls`）
  - `Planner_EachSend_UsesPlannedInstructionsToolsAndLabel`：第 1 輪是 `([Read], "指示甲", "監控與診斷")`，第 2 輪是 `([Read, Write], "指示乙", "帳號與權限")`，兩輪都回文字。斷言：
    - `handler.RequestBodies[0]` 含「指示甲」、不含 `write_things`。
    - `RequestBodies[1]` 含「指示乙」與 `write_things`，也含第一則使用者訊息（session 有延續）。
    - 對話紀錄中兩則 Assistant 的 `Agent` 依序是「監控與診斷」「帳號與權限」。
    - `chat.CurrentLabel == "帳號與權限"`。
  - `Planner_ApprovalResponse_DoesNotReplan`：這一輪含 Write，模型要求呼叫 `write_things`；`RespondAsync([id])` 之後，`PlanCalls == 1`，而且 `sample.WriteCalls == ["新內容"]`。
  - `Planner_Resume_UsesHomeTurn_AndRestoresPendingApproval`：在含 Write 的一輪停在核准；`CreateRunner().ResumeAsync(planner, id)` 不是 null，`PendingApprovals` 只有一筆 `寫入`；核准後 `WriteCalls` 有值。
  - `Planner_TurnWithDifferentName_Throws`：規劃器回傳的 `Definition.Name` 和 Home 不同，`SendAsync` 列舉時丟出 `InvalidOperationException`。
- [ ] **Step 2: 執行並確認失敗**
  執行 `dotnet test src/MyProject/MyProject.Tests --filter "FullyQualifiedName~AiAgentRunnerTests.Planner"`，預期編譯失敗（`IAiAgentTurnPlanner` 尚未定義）。
- [ ] **Step 3: 實作**
  - 把 `CreateAgentAsync` 拆成 `CreateHandle(AiAgentDefinition definition, string instructions, Guid conversationId)`。原本的路徑仍從 `promptProvider.GetAsync(definition.PromptTemplateKey)` 取指示；規劃器路徑則用 `AiAgentTurn.Instructions`。
  - `AiAgentChat` 的 `handle`、`Definition` 改成可替換的欄位。建構時另外收下 `IAiAgentTurnPlanner?` 和一個 `Func<AiAgentTurn, AiAgentHandle>`，用來建立 handle。
  - `SendAsync` 先做原本的同步檢查（待核准時丟出例外），再交給一個 async iterator 處理：有規劃器時先 `PlanAsync`，檢查名稱，替換 handle、Definition、`CurrentLabel`，然後才 `PersistingRunAsync`。`RespondAsync` 不重新規劃。
  - `FlushAssistant` 和工具紀錄的 `Agent` 欄位都填 `CurrentLabel`。沒有規劃器時維持 null，所以測試台不受影響。
  - 存檔的 AgentName 一律用 `Home.Name`（沒有規劃器時就是 `Definition.Name`）。
- [ ] **Step 4: 執行並確認通過**
  執行 `dotnet test src/MyProject/MyProject.Tests --filter "FullyQualifiedName~AiAgentRunnerTests|FullyQualifiedName~AiWorkflowRunnerTests"`，預期全部 PASS，舊測試也不能有任何失敗。

---

### Task 2: 領域與分派解析（純邏輯）

**Files:**
- Create: `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/AdminAssistantRouting.cs`
- Test: `src/MyProject/MyProject.Tests/AdminAssistantRoutingTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum AdminAssistantDomain { Accounts, Monitoring, Ai, Ops, Patrol }
  public static class AdminAssistantRouting
  {
      public const string DefaultLabel = "系統管理助理";
      public static IReadOnlySet<AdminAssistantDomain> Parse(string? text);
      public static string LabelOf(IReadOnlySet<AdminAssistantDomain> domains);
      public static string BuildRouterInput(string message, IReadOnlyList<AiAgentTranscriptEntry> transcript);
  }
  ```

- [ ] **Step 1: 寫失敗的測試**
  - `Parse("monitoring, ops")` → {Monitoring, Ops}
  - `Parse("PATROL")` → {Patrol}
  - `Parse("ai")` → {Ai}
  - `Parse("aim maintenance")` → 空集合（必須整字比對）
  - `Parse(null)`、`Parse("")` → 空集合
  - `LabelOf({Ops, Monitoring})` == `"監控與診斷、系統設定與維運"`（依 enum 順序，用頓號連接）
  - `LabelOf({Patrol})` == `"巡檢"`；`LabelOf({})` == `"系統管理助理"`
  - 中文名稱對照：Accounts 帳號與權限、Monitoring 監控與診斷、Ai AI 管理、Ops 系統設定與維運、Patrol 巡檢。
  - `BuildRouterInput`：對話紀錄有 10 則 User／Assistant，再加 Tool／Notice，結果只含最後 6 則 User／Assistant。每則截到 300 字，結尾是 `使用者這次的訊息：{message}`。
- [ ] **Step 2:** 執行 `dotnet test ... --filter "FullyQualifiedName~AdminAssistantRoutingTests"`，預期 FAIL（型別不存在）。
- [ ] **Step 3:** 實作。`Parse` 用不分大小寫的整字 regex：`\b(accounts|monitoring|ai|ops|patrol)\b`。
- [ ] **Step 4:** 重跑同一個 filter，預期 PASS。

---

### Task 3: 提示詞、模型、作業名稱，以及 Agent 定義的組裝

**Files:**
- Create: `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/AdminAssistantAgent.cs`
- Modify:
  - `src/MyProject/MyProject.AccessDatas/Models/PromptTemplate.cs`（`PromptTemplateKeys` 與 `All`）
  - `src/MyProject/MyProject.Web/Ai/AiPromptGuardrails.cs`（`DefaultInstructions`、`DataDescription`）
  - `src/MyProject/MyProject.Web/Ai/AiSystemPromptProvider.cs`
  - `src/MyProject/MyProject.Web/Components/Views/Admins/PromptTemplateView.razor.cs`（分頁名稱）
  - `src/MyProject/MyProject.Web/Configuration/AiModelSettings.cs`
  - `src/MyProject/MyProject.Web/Configuration/Parameters/SystemParameterCatalog.cs`
  - `src/MyProject/MyProject.Web/appsettings.json`（`AiModelSettings` 兩個空字串鍵）
  - `src/MyProject/MyProject.Models/Systems/TokenUsageModels.cs`（`TokenUsageOperations`）
  - `docs/` 的「日誌與設定檔說明」§4.15（加兩列）
  - 測試替身裡所有 `IAiSystemPromptProvider` 的實作（例如 `StubSystemPromptProvider`）
- Test: `src/MyProject/MyProject.Tests/AdminAssistantAgentTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `AdminAssistantDomain`、`AdminAssistantRouting.LabelOf`。
- Produces:
  - `PromptTemplateKeys.AdminAssistant`／`AdminAssistantRouter`／`AdminAssistantAccounts`／`AdminAssistantMonitoring`／`AdminAssistantAi`／`AdminAssistantOps`，字串值與常數名稱相同。
  - `TokenUsageOperations.AdminAssistant = "系統管理助理"`、`TokenUsageOperations.AdminAssistantRouter = "系統管理助理（分派）"`。
  - `AiModelSettings.AdminAssistant`、`AiModelSettings.AdminAssistantRouter`（string，`[MaxLength(MaxModelLength)]`）。
  - `IAiSystemPromptProvider.GetInstructionsAsync(string templateKey, CancellationToken cancellationToken = default)`：回傳作用中版本或內建預設，**不附加**鎖定規則。`GetAsync` 改成以它為基礎再 `Compose`。
  - `AdminAssistantAgent`（static）：
    - `const string Name = "AdminAssistant"`、`const string DisplayName = "系統管理助理"`
    - `IReadOnlyList<AiToolDefinition> ReadTools`（Task 5 才填入）
    - `IReadOnlyDictionary<AdminAssistantDomain, IReadOnlyList<AiToolDefinition>> MutatingTools`（M1 每個領域都是空清單）
    - `AiAgentDefinition DefinitionFor(IReadOnlySet<AdminAssistantDomain> domains)`：工具是 ReadTools 加上被選中領域的 MutatingTools；選到 Patrol 或空集合時只有 ReadTools。Operation、PromptTemplateKey、ModelKey 固定為 `TokenUsageOperations.AdminAssistant`、`PromptTemplateKeys.AdminAssistant`、`nameof(AiModelSettings.AdminAssistant)`。
    - `AiAgentDefinition Home`：全部工具。
    - `string ComposeInstructions(string baseText, IReadOnlyDictionary<AdminAssistantDomain, string> domainTexts, IReadOnlySet<AdminAssistantDomain> domains)`：先放 baseText，每個被選中的領域接一段 `## 專家：{中文名稱}` 加上該領域的文字。空集合或含 Patrol 時接上全部四個領域。**不含**鎖定規則，由呼叫端 Compose。
    - 6 個 `Default*Instructions` 常數。內容要求如下：
      - 共同說明：只能用工具查到的資料回答、不可編造；時間一律用伺服器本地時間；要異動時先確認對象；被拒絕就照實回報；巡檢報告的固定段落是「總結、健康狀態、例外與錯誤、排程與備份、資源用量、帳號與安全、建議處理」。
      - 分派說明：只回覆以逗號分隔的英文代碼 `accounts, monitoring, ai, ops, patrol`，不要其他文字，各代碼的意義也要寫在說明裡。
      - 四個領域各自的用語、工具使用時機與注意事項（例如：健康報告會實際做一次 AI 探測；查排程失敗原因時，用執行紀錄的 TraceId 去查例外與日誌）。

- [ ] **Step 1: 寫失敗的測試**（`AdminAssistantAgentTests`）
  - `DefinitionFor_AnyDomains_SharesNameOperationPromptAndModel`：對 {}、{Ops}、{Patrol} 以及四個領域全選，Name 都是 `AdminAssistant`、Operation 都是 `TokenUsageOperations.AdminAssistant`、ModelKey 都是 `nameof(AiModelSettings.AdminAssistant)`。
  - `ComposeInstructions_SelectedDomainsOnly`：選 {Monitoring}，結果含 base 和監控的文字，不含帳號的文字。
  - `ComposeInstructions_EmptyOrPatrol_IncludesAllFourDomains`
  - `AllTools_AreAdminOnly_UniqueSnakeCase_AndReadToolsNotMutating`：`Home.Tools` 名稱唯一且符合 `^[a-z][a-z0-9_]*$`；每個 `PermissionPage` 都在管理員專屬鍵清單裡（沿用 `AdminOnlyPermissionTests` 的清單，或把它抽成 internal 共用）；`ReadTools` 全部 `IsMutating == false`。
  - 既有的 `PromptTemplateTests`、`SystemParameterTests` 會檢查新的鍵是否登記完整，要一起跑。
- [ ] **Step 2:** 執行 `dotnet test ... --filter "FullyQualifiedName~AdminAssistantAgentTests|FullyQualifiedName~PromptTemplateTests|FullyQualifiedName~SystemParameterTests"`，預期 FAIL。
- [ ] **Step 3:** 依上面的 Interfaces 實作並完成各處登記。
- [ ] **Step 4:** 重跑同一個 filter，預期 PASS；再跑一次全部測試，確認替身修改沒有弄壞其他測試。

---

### Task 4: 系統管理助理的規劃器（分派）

**Files:**
- Create: `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/AdminAssistantTurnPlanner.cs`
- Modify: `src/MyProject/MyProject.Web/Extensions/ServiceCollectionExtensions.cs`（`AddScoped<AdminAssistantTurnPlanner>()`）
- Test: `src/MyProject/MyProject.Tests/AdminAssistantTurnPlannerTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IAiAgentTurnPlanner`、`AiAgentTurn`；Task 2 的 Routing；Task 3 的 `AdminAssistantAgent`、`IAiSystemPromptProvider.GetInstructionsAsync`。
- Produces: `public sealed class AdminAssistantTurnPlanner : IAiAgentTurnPlanner`，建構子參數為 `(IAiChatCompletionClient client, IAiSystemPromptProvider prompts, IOptionsMonitor<AiModelSettings> models, ILogger<AdminAssistantTurnPlanner> logger)`。`Home` 就是 `AdminAssistantAgent.Home`。

- [ ] **Step 1: 寫失敗的測試**（用假的 `IAiChatCompletionClient`，記下 request 並回覆預先排好的結果；假的 prompt provider 回傳 `"[鍵名]"` 當作內容）
  - `Plan_RouterSaysOps_LabelAndInstructions`：回覆 `"ops"`，則 `Label == "系統設定與維運"`；`Instructions` 含 `[AdminAssistant]` 和 `[AdminAssistantOps]`，不含 `[AdminAssistantAccounts]`，結尾是 `AiPromptGuardrails.For(PromptTemplateKeys.AdminAssistant)`。
  - `Plan_RouterRequest_UsesRouterOperationConversationAndModel`：request 的 `Operation == TokenUsageOperations.AdminAssistantRouter`、`ConversationId` 等於傳入值、Messages 是 system（分派提示詞）加 user（`BuildRouterInput` 的結果）。`AdminAssistantRouter` 有設定時 `ModelKey == nameof(AiModelSettings.AdminAssistantRouter)`，空白時是 `nameof(AiModelSettings.AdminAssistant)`。
  - `Plan_RouterFails_FallsBackToAllDomains`：回覆 `Success=false`，則 `Label == "系統管理助理"`，指示含四個領域，而且不丟例外。
  - `Plan_RouterReturnsProse_FallsBackToAllDomains`：回覆「我認為這是監控問題」，結果同上。
  - `HomeTurn_DoesNotCallRouter`：`HomeTurnAsync` 之後 client 的呼叫次數為 0，`Definition.Tools` 等於 `Home.Tools`。
- [ ] **Step 2:** 執行 `dotnet test ... --filter "FullyQualifiedName~AdminAssistantTurnPlannerTests"`，預期 FAIL。
- [ ] **Step 3:** 實作。分派呼叫的 `TimeoutHint`、`ContextLengthExceededMessage` 用簡短的中文；失敗時用 `LogWarning` 記下 Reason，不寫內容。
- [ ] **Step 4:** 重跑同一個 filter，預期 PASS。

---

### Task 5: 第一批唯讀工具（監控與診斷、排程）

**Files:**
- Create:
  - `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/AdminAssistantToolText.cs`（共用的時間解析、夾範圍、截斷）
  - `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/MonitoringAssistantTools.cs`
  - `src/MyProject/MyProject.Web/Ai/Agents/AdminAssistant/OpsAssistantTools.cs`
- Modify: `AdminAssistantAgent.ReadTools`（填入下列 8 個工具）、`ServiceCollectionExtensions.cs`（兩個工具類別都註冊為 scoped）
- Test: `src/MyProject/MyProject.Tests/AdminAssistantToolsTests.cs`

**Interfaces:**
- `AdminAssistantToolText`：
  - `static (DateTime Start, DateTime End, string? Note) ParseRange(string? start, string? end, DateTime now, TimeSpan defaultSpan, TimeSpan maxSpan)`：格式為 `yyyy-MM-dd HH:mm` 或 `yyyy-MM-dd`。解析失敗就用預設區間；超過上限就夾住起點。Note 用中文說明實際套用的區間。
  - `static int Clamp(int value, int min, int max)`
  - `static string Truncate(string? text, int max)`：超過就截斷並加上「…」。
  - `static readonly JsonSerializerOptions Json`：camelCase，不跳脫中文。
- 工具一律回傳 JSON 字串。時間格式為 `yyyy/MM/dd HH:mm:ss`（本地時間）。每個工具都在對應類別上以 `public static AiToolDefinition XxxTool { get; }` 宣告，寫法照 `ProjectAgentTools.SearchTool`。

| name | 方法 | PermissionPage（View） | 稽核 |
|---|---|---|---|
| `get_system_health` | `GetSystemHealthAsync()` → 各項目的名稱、狀態、訊息、總體狀態。說明裡要註明「會實際做一次 AI 探測並登入 SMTP」 | `角色_系統健康監控` | 不寫（頁面也沒寫） |
| `query_logs` | `QueryLogsAsync(string? start, string? end, string? minimumLevel, string? keyword, string? traceCode, int take = 50)`：預設最近 24 小時，最長 3 天；take 夾在 1～200；訊息截到 500 字 | `角色_日誌檢視` | `LogViewer.Search`（條件同頁面，再加 `source=admin-assistant`） |
| `get_log_level` | `GetLogLevel()` → 系統預設、覆寫、實際生效的等級 | `角色_日誌等級設定` | 不寫 |
| `search_exceptions` | `SearchExceptionsAsync(string? start, string? end, string? keyword, string? account, int take = 20)`：預設最近 7 天；take 夾在 1～50 | `角色_系統例外紀錄` | `ExceptionLog.Search` 加上 source |
| `get_exception_detail` | `GetExceptionDetailAsync(int id)`：堆疊截到 4000 字；找不到就回說明文字 | `角色_系統例外紀錄` | `ExceptionLog.View`，targetId=id，detail=`type=…; source=admin-assistant` |
| `get_database_usage` | `GetDatabaseUsageAsync()` | `角色_資料庫用量` | 不寫 |
| `get_scheduled_jobs_overview` | `GetScheduledJobsOverviewAsync()` → `SchedulingEnabled`、`SettingsError`、各排程（名稱、顯示名稱、cron、是否啟用、下次執行、上次執行的狀態／時間／耗時／訊息、是否逾期、是否排隊中），以及 `ScheduledJobAttention.From` 列出的需要注意項目 | `角色_排程作業` | 不寫 |
| `get_job_recent_runs` | `GetJobRecentRunsAsync(string jobName, int take = 10)`：take 夾在 1～50；jobName 接受名稱或顯示名稱，不存在時回覆可用的名稱清單；每筆含 Trigger、狀態、開始、耗時、訊息、TraceId | `角色_排程作業` | 不寫 |

- [ ] **Step 1: 寫失敗的測試**
  - `ParseRange_Invalid_UsesDefaultAndNotes`、`ParseRange_TooLong_ClampsStart`、`Clamp_Bounds`、`Truncate_AddsEllipsis`
  - `QueryLogs_ClampsTake_DefaultsRange_AndAuditsWithSource`：用假的 `ILogQueryService` 記下 request，`take=999`。斷言 request 的 `Take == 200`、區間是 24 小時；稽核替身收到 `AuditActions.LogViewer.Search`，detail 含 `source=admin-assistant`。
  - `GetJobRecentRuns_UnknownJob_ListsValidNames`：排程服務的替身或真實服務的建立方式，照既有排程測試的寫法。
  - `GetExceptionDetail_TruncatesStack_AndAuditsView`：資料庫建置照 `ExceptionLogService` 既有測試的 fixture。
- [ ] **Step 2:** 執行 `dotnet test ... --filter "FullyQualifiedName~AdminAssistantToolsTests|FullyQualifiedName~AdminAssistantAgentTests"`，預期 FAIL。
- [ ] **Step 3:** 實作 8 個工具，並把它們登記到 `ReadTools`。
- [ ] **Step 4:** 重跑同一個 filter，預期 PASS。Task 3 的「全部工具都是管理員專屬」測試此時涵蓋這 8 個工具。

---

### Task 6: 頁面、選單、權限與操作說明

**Files:**
- Create:
  - `src/MyProject/MyProject.Web/Components/Pages/Admins/AdminAssistantPage.razor`（`@page "/admin-assistant"` 加上 `<AdminAssistantView />`）
  - `src/MyProject/MyProject.Web/Components/Views/Admins/AdminAssistantView.razor`、`.razor.cs`、`.razor.css`
  - `src/MyProject/MyProject.Web/Datas/Help/admin-assistant.md`（UTF-8 含 BOM，固定七段，檔名要等於 `PageHelpService.ToSlugFileName("/admin-assistant")`）
- Modify:
  - `MyProject.Share/Helpers/MagicObjectHelper.cs`：新增 `角色_管理助理 = "管理助理功能"`（子分類）與 `角色_系統管理助理 = "系統管理助理"`
  - `Datas/Menu.json`：在系統管理（id 3）底下新增子分類「管理助理」與頁面「系統管理助理」，id 和圖示都不能和現有的重複
  - `Components/Layout/SidebarMenuService.cs`：在 `MenuPermissionMap` 加上這兩個 id
  - `Datas/HelpTopics.json`：title 要和選單名稱「系統管理助理」相同
  - `MyProject.Tests/AdminOnlyPermissionTests.cs`、`MyProject.Tests/MenuPermissionConsistencyTests.cs`（把 View 加進 `AdminOnlyViews`），新圖示加進 `MyProject.Tests/MenuIconTests.cs` 的允許清單
  - `Components/Commons/OverlayStyles.razor`（核准窗與重新命名窗的寬度）、`wwwroot/theme.css`（頁面內的新樣式）
- [ ] **Step 1: 守門測試先失敗**：把 View 加進 `AdminOnlyViews`，把兩個鍵加進 `AdminOnlyPermissionTests`。執行 `dotnet test ... --filter "FullyQualifiedName~AdminOnlyPermissionTests|FullyQualifiedName~MenuPermissionConsistencyTests|FullyQualifiedName~PageHelpCatalogTests|FullyQualifiedName~MenuIconTests|FullyQualifiedName~ThemeConventionTests"`，預期 FAIL。
- [ ] **Step 2: 畫面**：版面與互動照 `AgentPlaygroundView`，以下是差異：
  - 權限檢查用 `CheckIsAdmin()`；檢查沒通過前什麼都不渲染，被拒時呼叫 `RecordPageAccessDeniedAsync("/admin-assistant")`。
  - 開對話與接續改用 `runner.StartAsync(planner)`、`runner.ResumeAsync(planner, id)`；「我的對話」用 `ListMineAsync(AdminAssistantAgent.Name)`。
  - 每則 Assistant 上方顯示「由 {Agent} 回答」（Agent 是 null 時不顯示）。執行中顯示「分派中…」，等到第一筆更新抵達為止。
  - 空白對話顯示 5 個範例問句，點一下會填進輸入框：「幫我巡一下系統」「最近排程的執行狀況」「昨晚備份為什麼失敗」「哪些帳號超過 180 天沒登入」「把日誌等級調成 Debug」。
  - 拿掉測試台專屬的「測試專案」按鈕與說明窗。
- [ ] **Step 3: 選單、權限、說明的登記**（依上面的 Files）。說明文件的第三段要列出所有按鈕與欄位。
- [ ] **Step 4:** 重跑 Step 1 的 filter，預期 PASS。

---

### Task 7: 文件、版本與實機驗證

**Files:**
- Modify:
  - `src/MyProject/MyProject.Web/appsettings.json`：`SystemSettings.SystemInformation.SystemVersion` 改為 `0.9.155 (2026/11/06)`
  - `docs/prd/系統管理助理-prd.md`：標示 M1 已實作
  - `docs/architecture/MAF-AI呼叫架構.md`：在 §3／§6 加上每輪規劃器的說明
  - `docs/architecture/開發慣例與限制速查.md`：加上 Agent 規劃器的不變量（所有 Name 必須相同、核准回覆不重新分派）
  - `docs/prd/AI-Agent-prd.md`：加上連結
  - CI-CD 與品質檢查文件中的測試數
- Create: `docs/changelog/2026-11-06-系統管理助理M1.md`，並加進文件索引

- [ ] **Step 1:** 執行 `pwsh ./scripts/Invoke-QualityGate.ps1`，預期七步全部通過。
- [ ] **Step 2: 實機驗證**（依記憶「AI 修正要實際跑網站驗證」：先實打 API 驗證假設，再用 `dotnet run` 加 gstack browse 實測）
  - 以管理員登入 `/admin-assistant`。
  - 問「最近排程的執行狀況」：標籤是「系統設定與維運」，回答內容和 `/scheduled-jobs` 頁面一致。
  - 接著問「最近有什麼例外」：標籤是「監控與診斷」，第二輪仍記得前文。這一步同時驗證**同一個 session 換工具組**時，Responses API 能接受歷史中有前一輪的 function_call。如果上游回 400，就把 Task 1 改成「session 累積載入過的工具聯集」，並補上測試後再驗證一次。
  - 重新整理頁面，從「我的對話」接續，再問一題，能正常回答。
  - 到 `/ai-call-logs` 確認同一個 ConversationId 有分派呼叫和主呼叫；`/token-usage` 出現兩個新的作業名稱；`/audit-logs` 有 `LogViewer.Search`／`ExceptionLog.Search`，detail 含 `source=admin-assistant`。
  - 以非管理員登入：選單看不到這一頁；直接打網址會被拒，並寫入 `Permission.Denied`。
- [ ] **Step 3:** 整理變更摘要與建議的 commit message（含「版本號 0.9.154 → 0.9.155」），**交給使用者決定是否 commit**。

---

## 後續里程碑（另寫計畫）
- **M2**：帳號、AI、維運的其餘唯讀工具，以及 `patrol` 巡檢報告（成功標準 ①②）。
- **M3**：可復原的異動工具、批次清單（最多 50 筆）、自我保護與最後一位管理員保護、核准窗的長度上限放寬（成功標準 ③）。
- **M4**：不可復原的異動工具，以及 `AiToolDefinition.IsIrreversible` 和紅字核准窗。
