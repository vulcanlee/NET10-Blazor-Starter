# WASM 遷移 M8：Agent、系統管理助理與通知推播（0.10.7）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.10.7
- 首次實作版本：0.10.7
- 最後核對日期：2026/11/11

## 目的

WASM 遷移的第八個里程碑（計畫見 [WASM 遷移計畫](../migration/WASM遷移計畫.md)）：「Agent 測試台」（`/agent-playground`）、「Agent 工作流程」（`/agent-workflows`）、「系統管理助理」（`/admin-assistant`，含巡檢報告窗與 `?patrolReport=` 深連結）改在使用者的瀏覽器以 WebAssembly 執行，背後是新的 Web API。
同時加上通知推播串流：WASM 外框以一條長連線即時收到「通知／公告有變動」，鈴鐺、公告橫幅與首頁「我的通知」在其他分頁與其他使用者那邊也通常幾秒內更新。其他頁面維持原狀。
測試報告見 [M08-Agent系統管理助理與通知推播](../migration/測試報告/M08-Agent系統管理助理與通知推播.md)。

## 變更內容

### 1. 三個頁面改用 WebAssembly

- 頁面與檢視搬到 `MyProject.Web.Client`（`Pages/AgentPlaygroundPage.razor`、`AgentWorkflowsPage.razor`、`AdminAssistantPage.razor`；`Views/AiAssistants/AgentPlaygroundView`、`AgentWorkflowsView`；`Views/Admins/AdminAssistantView`、`AdminPatrolReportsModal`），Server 版已刪除。畫面、按鈕、對話窗與訊息和以前相同。
- 資料經 `Web.Client/Api/AgentConversationApi.cs`（`AgentPlaygroundApi`、`AdminAssistantApi`）、`AgentWorkflowApi.cs` 呼叫 API，DTO 對應 `AgentDtoMapper`、`AgentWorkflowDtoMapper`。DTO 在 `MyProject.Dtos/Ai`（`AgentDtos`、`AgentStreamDtos`、`AgentWorkflowDtos`、`WorkflowStreamDtos`）、`MyProject.Dtos/Admins/AdminPatrolReportDtos.cs` 與 `MyProject.Dtos/Shell/PushDtos.cs`。
- 搬到共用的位置：`AdminAssistantApproval`（不可復原的動作要輸入「確認」的判斷）與 `AiToolApprovalRequest` 搬到 `MyProject.Web.Client/Ai/`（命名空間不變），伺服器與核准窗共用。
- 目前以 WebAssembly 執行的頁面共 25 個：M2～M7 的 22 個加上這次的 3 個。仍是 Server 的：`/`（導到 `/App`）、`/ChangePassword`、`/TwoFactorSetup`、`/Error`、`/not-found` 與登入相關頁（M9 搬）。

### 2. 新的 Web API

全部只有 `api/v1` 路由、要 Bearer、回 `ApiResult`；對話、執行與推播回 NDJSON 串流。端點細節見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md) 第二之四節與第五之一節。

- **`api/v1/agent-playground`**（`AgentPlaygroundController`，`[HasPermission(角色_Agent測試台, View)]`）與 **`api/v1/admin-assistant`**（`AdminAssistantController`，`[AdminOnly]`），流程共用 `Controllers/AgentConversationEndpoint.cs`：`GET info`（AI 可用、保存天數、推論等級選項、測試專案權限與標題前綴）、`GET conversations`、`GET conversations/{id}`（完整紀錄＋待核准）、`PUT conversations/{id}/title`、`DELETE conversations/{id}`（`AiAgentConversation.Delete`）、`POST messages`（NDJSON）、`POST conversations/{id}/approvals`（NDJSON）。測試台另有 `POST`／`DELETE samples`（另要專案項目的新增／刪除），系統管理助理另有 `GET patrol-reports`、`GET patrol-reports/{id}`。
- **`api/v1/agent-workflows`**（`AgentWorkflowsController`，`[HasPermission(角色_Agent工作流程)]`）：`GET info`、`GET records`、`GET records/{id}`、`PUT records/{id}/title`、`DELETE records/{id}`、`POST runs`（NDJSON）、`GET runs/active`、`POST runs/{id}/responses`（NDJSON）、`POST runs/{id}/stop`、`POST runs/{id}/abandon`（`?onlyIfIdle=true` 用於離開頁面）。
- **`POST api/v1/notifications/stream`**（`NotificationsController`，`[SignedInUser]`）：通知推播串流，見第 5 節。`GET api/v1/shell/info` 多回 `NotificationPushEnabled`。
- 稽核與 Server 版相同、都在原本的服務裡（工具外殼、對話服務、專案服務）；開啟、改名、看巡檢報告不寫。

### 3. Agent 對話：每個請求接續一次、每段對話一把鎖

- **每個請求以 `AiAgentRunner.ResumeAsync` 接續已存檔的對話**：伺服器不把對話物件留在記憶體，送訊息與回覆核准都是「讀存檔 → 接續 → 執行 → 存檔」，所以每個請求用當下系統參數的模型。串流每行：`start`（對話 ID、模型、推論等級）、`entry`（新增的紀錄）、`delta`、`tool`、`label`（由誰回答，每個請求至少送一次）、`approvals` 或 `result`、閒置 `ping`。中斷連線取消這次執行，已收到的照樣存檔。
- **規則搬到伺服器**：訊息 2,000 字上限（400）、等待核准時送新訊息 409、核准編號必須在目前待核准裡（否則 409，所以同一核准送兩次只執行一次）、沒有待核准卻送核准 409、不可復原動作沒輸入「確認」400。
- **併發**：`AiAgentConversationLocks` 每段對話一把鎖，兩個分頁同時操作同一段對話時後到的**排隊**（Server 版沒有鎖，可能互相覆蓋）——先回 200 串流、等待期間送心跳，輪到才驗證，錯誤放在 `result` 那一行。核准以 `HasPendingApproval` 條件式更新搶占，跨行程也只會執行一次。
- **系統管理助理核准後沿用原本分派到的領域**：分派結果代碼存進對話檔的 `TurnKey`（`AiAgentTurn.Key`，只加不改），接續時 `IAiAgentTurnPlanner.RestoreTurnAsync` 重建同一輪（不呼叫分派模型）。
- **網址帶 `?conversation={id}`**：重新整理回到同一段對話，停在核准的會再跳出核准窗（比 Server 版多的功能）。
- **對話內容檔讀寫改為共用模式**（`AiAgentConversationFileStore`）：另一個請求正在讀時寫入不會失敗，避免「待核准」旗標與內容檔不一致而卡死（見第 7 節第 2 項）。

### 4. Agent 工作流程：伺服器端執行登錄表

- 一次執行要跨好幾個請求，執行物件放在 `AiWorkflowRunRegistry`（singleton）：每次執行有自己的 `AsyncServiceScope`（填入目前使用者與來源 IP，每次回覆更新）；只有擁有者能操作；閒置 30 分鐘結束成「中斷」並釋放 scope；網站關閉時先中斷、等收尾最多 5 秒再釋放。
- **停在等人回覆時重新整理或關分頁，執行留在伺服器最多 30 分鐘，回到頁面自動接回**（`GET runs/active`，比 Server 版多的功能）；串流途中斷線仍中斷整個流程（同 Server 版「離開頁面就中斷」）；在站內切到別的頁面時，只有這個分頁開始或回覆過的執行才放棄，而且另一個請求正在用時不放棄。
- **一人同時只能有一次執行**（第二個開始回 409）；別人的執行 404；驗證失敗 400（回答空白、退回沒寫意見、次數用完）；重複回覆或正在處理 409（畫面收到 409 會重讀狀態）。
- ⚠️ 已知限制：登錄表只在單一行程有效，IIS 回收或重啟後執行就沒了（紀錄停在最後一次存檔）。

### 5. 通知推播串流

- 串流：`ready` → 有變動送 `notifications` 或 `announcements`（不帶內容，短時間多次合併）→ 閒置每 25 秒 `ping`；伺服器主動結束時最後一筆是 `renew`（存取 Token 到期前 1 分鐘，前端刷新後重連）或 `reconnect`（滿 30 分鐘、網站關閉）。
- 產生端（`Streaming/NotificationPushStream.cs`）自己監看 `RequestAborted` 並取消訂閱；每位使用者最多 3 條推播連線（第 4 條起 429，那些分頁只靠輪詢）；推播連線不記慢請求。
- 開關 `NotificationSettings:PushEnabled`（預設 `true`；`false` 時端點 404、外框不連）。
- WASM：`Layout/ClientPushConnection.cs`（外框啟動一條，收到變動呼叫 `ClientNotificationSignal`／`ClientAnnouncementSignal` 立即重讀；斷線 2→4→…→60 秒退避；401 經 `ApiClient` 刷新；401／403／404 不再重試）。原本 60 秒輪詢保留為備援。
- HTTP/1.1 每網域 6 條連線的限制：正式環境 IIS＋HTTPS 通常是 HTTP/2 不受影響；本機 http 是 HTTP/1.1，每人上限 3 條推播保留另外 3 條給一般 API。沒做跨分頁共用一條連線（BroadcastChannel），理由是複雜度與 HTTP/2 已解決。

### 6. 共用基礎：串流心跳

- `MyProject.Web/Streaming/NdjsonHeartbeat.cs`：`WithHeartbeat(source, interval, ping)`，資料源閒置時插入 ping（Agent／工作流程 15 秒、推播 25 秒），避免代理伺服器把閒置連線切斷。資料源丟的例外照樣丟出；取消由資料源自己處理。

### 7. 獨立審查（5 項，全部修正）

1. 中：工作流程第二個分頁接回來看一下就離開，會中斷第一個分頁正在跑的流程 → 只放棄這個分頁開始或回覆過的執行，伺服器在有別的請求使用中時不放棄（`onlyIfIdle`）；收到 409 時重讀狀態。
2. 中低：對話內容檔同時讀寫時寫入可能失敗，「待核准」旗標與內容檔不一致，對話永久卡死 → 讀寫改用共用模式並重試；補測試 `AiAgentConversationFileStoreTests`。
3. 低：排隊中按「停止產生」，畫面補上伺服器沒有存的紀錄 → 改為重讀伺服器上的對話。
4. 低：系統管理助理核准後那一輪收不到「由誰回答」標籤 → 每個請求至少送一次。
5. 低：網站關閉時執行登錄表在串流中途就釋放 scope → 先中斷、等收尾（最多 5 秒）再釋放。

### 8. 與 Server 版不同、使用者看得到的地方

1. Agent 測試台與系統管理助理的網址帶對話，重新整理回到同一段對話（停在核准的再跳出核准窗）。
2. 兩個分頁同時操作同一段對話時後到的排隊，兩則都完成；同一個核准只執行一次。
3. 工作流程停在等人回覆時重新整理或關分頁，30 分鐘內回到頁面自動接回；一人同時只能有一次執行。
4. 按「停止產生」後畫面自己補「已停止產生…」那幾則（M6 的做法）；還在排隊就按停止則改為重讀伺服器上的對話。
5. 改推論等級改成下一則訊息才在伺服器套用；每個請求用當下系統參數的模型（Server 版要按「新對話」或重新接續）。
6. API 錯誤改成一般錯誤通知（訊息照抄伺服器）。
7. 鈴鐺、公告橫幅、首頁「我的通知」在其他分頁與其他使用者那邊通常幾秒內更新（M7 時其他分頁最慢 60 秒）；推播關閉或斷線時退回 60 秒輪詢。

### 9. 守門測試調整

- `PageAuthorizationTests` 呼叫 `Check()` 的元件掃描下限降為 1（只剩兩步驟驗證設定）。
- `AdminPatrolNotificationTests` 改讀 Client 的頁面；`Components/AdminPatrolReportsModalTests` 改寫為 WASM 版（資料以假的 API 提供）。
- `LoggingConventionTests` 的純轉換白名單加 `AgentWorkflowMapper.cs`；`LoggingPipelineHealthTests` 加「推播串流不算慢請求」。

### 10. 權限不變

- Agent 測試台、Agent 工作流程仍是角色矩陣裡的頁面權限（不自動開給預設角色）；系統管理助理仍只限管理員；推播登入即可、只推自己的通知與公告。

### 11. 測試

- 測試 2,885 → 2,970。新增 `AgentConversationApiTests`（22）、`AgentWorkflowApiTests`（17）、`NotificationPushApiTests`（19）、`Client/ClientPushConnectionTests`（13）、`NdjsonHeartbeatTests`（3）、`AiAgentConversationFileStoreTests`（3）、`Client/AgentDtoMapperTests`（3）；`AiAgentRunnerTests` +2（每新增一則紀錄依序通知、以 `TurnKey` 接續同一輪）、`AdminAssistantTurnPlannerTests` +2（以領域代碼重建同一輪）等。
- 實機驗證腳本：`scripts/e2e/m08-views.mjs`（4 個初始畫面比對：Agent 測試台、Agent 工作流程、系統管理助理、巡檢報告窗）、`m08-agents.mjs`（真實模型：核准與拒絕、核准中重新整理、兩分頁排隊、工作流程人工介入中重新整理接回到完成、不可復原要輸入「確認」、巡檢報告連結、推播 2 秒內到達，最後跑全部路由煙霧測試；`nopush` 參數：關閉推播仍可輪詢，主機加 `NotificationSettings__PushEnabled=false`）。用法見 [`scripts/e2e/README.md`](../../scripts/e2e/README.md)。
- 實機注意：真實模型常會先回問「請確認是否要修改？」再呼叫寫入工具，腳本會回答「確認」。

## 升版注意

- 版本號 0.10.6 → 0.10.7。
- 沒有資料庫異動（系統管理助理的 `TurnKey` 存在對話內容檔，只加不改，舊檔讀出來是 null）。
- 新設定 `NotificationSettings:PushEnabled`（預設 `true`）。部署前確認：IIS 與反向代理不緩衝 `application/x-ndjson`、閒置逾時大於推播心跳（25 秒，建議 120 秒以上）、建議 HTTP/2、應用程式集區維持一個背景工作處理序（工作流程登錄表、對話鎖與推播連線數都在單一行程內）。見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)「Agent、工作流程與通知推播的長連線」。
- IIS 回收或重新部署時，停在等人回覆的工作流程會變成「中斷」；推播連線會自動重連。
- 衍生專案若改過三個頁面的 Server 版（`Components/Pages/AiAssistants/*`、`Components/Pages/Admins/AdminAssistantPage.razor`、對應的 `Components/Views/*`）：改動要搬到 `MyProject.Web.Client` 的同名頁面與檢視，資料改經 `api/v1/agent-playground`、`api/v1/admin-assistant`、`api/v1/agent-workflows`。
- 衍生專案若新增了有每輪規劃器（`IAiAgentTurnPlanner`）的 Agent：實作 `RestoreTurnAsync`（預設退回 `HomeTurnAsync`），否則 WASM 版核准後會用預設的一輪繼續。
- 衍生專案若引用 `AdminAssistantApproval` 或 `AiToolApprovalRequest`：檔案搬到 `MyProject.Web.Client/Ai/`，命名空間不變。
