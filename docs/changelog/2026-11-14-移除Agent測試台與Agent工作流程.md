# 移除 Agent 測試台與 Agent 工作流程（0.10.22）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.10.22
- 首次實作版本：0.10.22
- 最後核對日期：2026/11/14

## 目的

腳手架的理念是：從它建立新專案時，不帶出多餘的資料、程式或系統功能，新系統需要時再自行設計。但腳手架要具備 Microsoft Agent Framework（MAF）的能力，因為未來的專案都會用到 AI agent。

所以這次移除側邊選單「AI 助理」整個區塊，保留 MAF 的基礎建設，並以「系統管理助理」當作實際範例。「AI 助理」區塊包含兩頁：

- 「Agent 測試台」`/agent-playground`
- 「Agent 工作流程」`/agent-workflows`

移除前的完整版本已標記為 git tag `AI助理-0.10.21`，需要時可以從那裡取回。

## 使用者決定（2026/11/14）

| 項目 | 決定 |
|---|---|
| 多 Agent 工作流程引擎 | 一起移除（頁面拿掉後沒有其他使用者） |
| 專案工具與「產生測試專案」 | 移除 |
| 系統管理助理（含定時巡檢） | 保留 |
| 既有資料庫的舊資料 | 以只刪資料的 migration 清掉；兩頁的對話紀錄交給既有的清理排程依保留天數刪 |
| 文件 | 說明類文件同步改寫，整份只講這兩頁的刪除；歷史紀錄（變更紀錄各篇、`docs/migration`）不動 |

## 移除的內容

### 畫面、API 與 DTO

- **畫面**：`Pages/AgentPlaygroundPage.razor`、`AgentWorkflowsPage.razor`，以及 `Views/AiAssistants/` 整個資料夾。
- **前端 API**：`AgentWorkflowApi`、`AgentWorkflowDtoMapper`、`AgentPlaygroundApi`。
- **伺服器 API**：`api/v1/agent-playground`（含 `/samples`）、`api/v1/agent-workflows`，連同 `AgentPlaygroundController`、`AgentWorkflowsController`、`AgentWorkflowMapper`。
- **DTO**：
  - 刪 `AgentWorkflowDtos`、`WorkflowStreamDtos`、`AgentSampleResultDto`。
  - `AgentPageInfoDto` 刪掉只有測試台用的 `CanCreateSamples`、`CanClearSamples`、`SampleTitlePrefix`。

### AI 程式

- 範例 Agent `AgentPlaygroundAgent`，以及專案工具 `ProjectAgentTools`（搜尋、修改描述、明細、附加狀態摘要）。
- 「產生／清除測試專案」`ProjectSampleDataService`。
- 兩條工作流程「專案狀態報告」「多觀點審查」。
- 整個工作流程引擎：`AiWorkflowRunner`、`AiWorkflowRunRegistry`、`AiAgentStep`、`AiWorkflowDefinition`、`AiWorkflowModels`。
- 套件 `Microsoft.Agents.AI.Workflows`。
- `ClientIpProvider.SetCircuitClientIp`：只有工作流程登錄表在用，移除後沒有呼叫端；`IClientIpProvider` 改為直接註冊。

### 選單與權限

- `Menu.json` 的 id 9、91、92，以及 `MenuPermissionMap` 的對應項目。
- `MagicObjectHelper` 的 `角色_AI助理`、`角色_Agent測試台`、`角色_Agent工作流程`，以及角色權限矩陣的「AI 助理」這一組。
- `RolePermissionService.NotGrantedToDefaultRole`（不自動開給預設角色的頁面）只放這兩頁，拿掉後是空集合、沒有作用，所以一併移除。預設角色從此擁有全部頁面。

### 設定、提示詞、用量

- **系統參數與 `appsettings.json`**：
  - `AiModelSettings` 的 `AgentPlayground`、`ProjectStatusReport`、`MultiPerspectiveReview`
  - `AiReasoningSettings` 的同名三項
- **提示詞**：8 個種類（`AgentPlayground`，以及 7 個 `ProjectReport*`、`Review*`）。
  - 「AI 提示詞」頁剩兩組 8 份：「一般」（日誌分析、例外分析）、「管理助理」（6 份）。
- **Token 用量**：作業名稱 `Agent 測試台`、`專案狀態報告`、`多觀點審查` 不再產生。舊紀錄保留原本的名稱，直到依保留天數清理。
- **資料保留頁**：「Agent 對話」列的「前往」改連 `/admin-assistant`，說明拿掉工作流程。

## 保留的 MAF 基礎建設

以下保留，系統管理助理（`/admin-assistant`，含每天的定時巡檢）就是使用它們的完整範例：

- **AI 呼叫**：`AiAgentFactory`（所有 AI 呼叫的唯一入口）。
- **Agent 執行**：`AiAgentRunner` 與 `AiAgentChat`、`AiAgentDefinition`、`AiAgentTurn`、`AiAgentConversationLocks`。
- **工具**：`AiAgentToolFactory`、`AiToolApprovals`、`IAiToolPermissionGuard`。
- **共用的對話 API**：`AgentConversationEndpoint`。
- **對話保存與清理**：對話表 `AiAgentConversation`、檔案庫，以及「Agent 對話清理」排程。
- **依對話選推論等級**：`AiCallContext.ReasoningEffortOverride`、`AgentConversationProfile.SupportsReasoningEffort`。目前沒有頁面開放這項設定，但它是 Agent 執行的一部分，所以保留。

## 資料庫

**結構不變**：沒有任何資料表或欄位只給這兩頁用。

- 對話表與系統管理助理共用，以 `AgentName` 區分。
- 專案工具寫的是既有的「描述」欄位。

新增只刪資料的 migration `20261114090345_RemoveAgentPagesData`（EF Core 11 產生），清掉既有資料庫的舊資料：

- 3 個權限鍵的 `Permission` 與 `RolePermissionMap`。
- 各角色的權限清單 `RoleView.TabViewJson` 去掉這 3 個鍵，其餘照原順序；不是合法 JSON 的不動。
- 8 個種類的 `PromptTemplate` 版本。
- 6 個系統參數鍵。
- `Down` 不還原：刪的是已不存在的功能的資料。

這兩頁的對話與執行紀錄不在 migration 裡刪，交給「Agent 對話清理」排程依保留天數連同內容檔一起刪。

## 測試

- **刪除**：
  - `AgentWorkflowApiTests`、`AiWorkflowRunnerTests`、`ProjectSampleDataServiceTests`
  - `AgentToolsAndModelTests`：其中選模型的測試搬到新的 `AiModelSelectionTests`
- **改寫**：
  - `AgentConversationApiTests`：共用對話流程（送出、接續、2,000 字、別人的對話、中斷、核准與拒絕、同時核准、排隊、改名刪除）改經 `/api/v1/admin-assistant` 驗證；核准改用系統管理助理的「發送測試通知」。只為測試台存在的案例（測試專案、測試台權限、經 API 選推論等級）拿掉。
  - 其餘改用系統管理助理的鍵或名稱：`AiAgentPipelineTests`、`AiReasoningSettingsTests` 等。
- **新增**：`AgentPagesDataCleanupMigrationTests`，並登記到 `New-StarterProject.ps1` 的 `$starterMigrationTests`。
- **e2e**：
  - `smoke-routes.mjs` 拿掉兩個路由。
  - `m08-views.mjs` 拿掉 01～02。
  - `m08-agents.mjs` 拿掉 M8-G1～G5。
  - `explore-edge.mjs`、`m06-*.mjs` 不受影響。

## 版本

0.10.21 → 0.10.22（與 [AI 快速模型 FastModel](2026-11-14-AI快速模型FastModel.md) 同一版）。
