# DTO 與模型邊界規範

- 文件版本：1.5
- 文件狀態：已實作
- 現行系統版本：0.10.5
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/11

## 目標
- [x] 明確定義 API、UI、Business、Entity 的資料邊界，避免腳手架日後擴充時直接暴露資料庫 Entity。

## 邊界原則
- [x] Web API request/response 只使用 `MyProject.Dtos`，不可直接接收或回傳 `AccessDatas.Models` Entity。
- [x] Entity 僅作為 EF Core persistence model 使用。
- [x] AdapterModel 目前主要供 Blazor UI/後台頁面使用，不應與 API DTO 混用。
- [x] Controller 使用 AutoMapper 或明確 mapping 在 DTO 與 Entity 之間轉換。
- [x] 0.10.2 起（WASM 遷移 M3）走服務的 Controller 是在 DTO 與 AdapterModel 之間轉換（服務收發 AdapterModel）：對應寫在 `MyProject.Web.Client/Api/` 的 `CategoryDtoMapper`、`TeamDtoMapper`（0.10.3 起再加 `ProjectDtoMapper`，0.10.4 起再加 `UserDtoMapper`、`RoleDtoMapper`，0.10.5 起再加 `ExceptionLogDtoMapper`、`AiCallLogDtoMapper`、`TokenUsageDtoMapper`），WASM 頁面（API 回來的 DTO → 畫面模型）與伺服器的 Controller（請求 DTO → 畫面模型 → 服務）共用同一份，不另用 AutoMapper。API 的邊界仍然只有 DTO，AdapterModel 不出現在 request／response。

## 新 CRUD 模組待辦
- [ ] 新增 `XxxDto` 作為 response DTO。
- [ ] 新增 `XxxCreateUpdateDto` 作為 create/update request DTO。
- [ ] 新增 `XxxSearchRequestDto` 作為查詢 request DTO。
- [ ] 新增 mapping profile，並補 DTO 欄位驗證 attribute。
- [ ] 新增 integration tests，確認 JSON body 不含不該暴露的 Entity 導覽屬性或內部欄位。

> 0.9.110 起 `scripts/New-CrudModule.ps1` 會一併產生前四項：`XxxDto`、`XxxCreateUpdateDto`（含 `[Required]`／`[StringLength]`）、
> `XxxSearchRequestDto` 與 DTO 對應（0.10.2 起是 `Web.Client/Api/XxxApi.cs` 裡的 `XxxDtoMapper`）；0.10.2 起另產生 API 整合測試 `XxxApiTests`（完整生命週期與稽核、409、匯出、403），與服務層測試 `XxxServiceTests` 並存。

## 驗收標準
- [x] `ProjectController`、`CategoryController`、`TeamController` 目前均使用 DTO 作為 API 邊界（0.10.2 起分類、團隊另有 `CategoryFormOptionsDto`、`TeamNodeDto`、`SetEnabledRequestDto`，`CategoryDto`／`TeamDto` 多了 `IsDeleted`、`DeletedAt`、`DeletedBy`；0.10.3 起專案另有 `ProjectFormOptionsDto`、附件 `ProjectFileDto`（不含伺服器上的存放路徑）、下載票證 `DownloadTicketDto`，`ProjectDto` 多了 `Files`（只有單筆 `GET {id}` 帶）與 `IsDeleted`、`DeletedAt`、`DeletedBy`，`ProjectCreateUpdateDto` 的 `StartDate`／`EndDate` 改成可為 null）；`AuthController` 用 `MyProject.Dtos/Auths/` 的 `LoginRequestDto`、`RefreshTokenRequestDto`、`TokenResponseDto`、`CurrentUserDto`。
  `DownloadsController`（0.10.3 起以票證下載附件）、`BackupController` 與各控制器的匯出成功時回原生檔案，錯誤回 `ApiResult`，不經 DTO。
- [x] 0.10.4 起（WASM 遷移 M5）只限管理員的 `UsersController`、`RolesController`、`AuditLogsController`、`EmailLogsController` 用 `MyProject.Dtos/Admins/`：
  - `UserDtos.cs`：`UserSearchRequestDto`、`UserDto`（`Password` 只用於寫入、讀取一律是空的；清單每列帶 `CanDelete`／`CanCopy`／`CanToggleStatus`／`CanForceLogout`／`CanResetTwoFactor` 與伺服器算好的 `LockedText`；新增時 `CopiedFromAccount`）、`UserFormOptionsDto`／`UserRoleOptionDto`、`UserCopyDraftDto`、`UserSaveResultDto`（`SessionRefreshUrl`）、`EmailLookupRequestDto`。
  - `RoleDtos.cs`：`RoleSearchRequestDto`、`RoleDto`、樹狀的權限矩陣 `RolePermissionDto` → `RolePermissionGroupDto` → `RolePermissionNodeDto`（`Actions`：動作名稱 → 是否勾選）、`RoleFormOptionsDto`。
  - `AuditLogDtos.cs`：`AuditLogSearchRequestDto`、`AuditLogItemDto`（繼承異動歷程的 `AuditHistoryItemDto`，多 `OccurredAtUtc` 與簽章）、`AuditLogDetailDto`、`AuditLogOptionsDto`、`AuditLogPurgeResultDto`、`AuditIntegrityReportDto`／`AuditIntegrityIssueDto`。
  - `EmailLogDtos.cs`：`EmailLogSearchRequestDto`、`EmailLogItemDto`。
- [x] 0.10.5 起（WASM 遷移 M6）日誌與 AI 管理頁的 API（全部 `[AdminOnly]`）：
  - `MyProject.Dtos/Ai/AiStreamDtos.cs`：NDJSON 串流的每一行 `AiStreamFrameDto`（`Kind` 為 `AiStreamFrameKinds` 的 `start`／`delta`／`result`；start 帶 `AiStreamScopeDto`、delta 帶 `Text`、result 帶 `AiResultDto`）、用量 `AiUsageDto`、對話一則 `AiChatMessageDto`（`Role`、`Content`、被停止的回覆標 `Interrupted`）。伺服器的 `AiStreamUpdate`／`AiAnalysisResult` 不出現在 API 邊界，經 `MyProject.Web/Ai/AiStreamFrames.cs` 轉換。
  - `MyProject.Dtos/Ai/AiCallLogDtos.cs`：`AiCallLogSearchRequestDto`、`AiCallLogPageDto`、`AiCallLogContentSearchDto`、`AiCallLogItemDto`、`AiCallLogOptionsDto`、`AiCallLogDetailDto`、`AiCallLogMessageDto`、`AiCallLogTextDto`、`AiCallLogPurgeRequestDto`、`AiCallLogMergedPdfRequestDto`。
  - `MyProject.Dtos/Admins/ExceptionLogDtos.cs`：`ExceptionLogSearchRequestDto`、`ExceptionLogItemDto`、`ExceptionLogDetailDto`、`ExceptionLogStackTraceDto`、`ExceptionLogCopyTextDto`、`ExceptionLogOptionsDto`、`ExceptionAiRequestDto`（`ConversationId`＋例外明細之後的問答；追問上限 `MaxQuestionLength` 2,000 字、回覆上限 `MaxReplyLength` 512K 字）、`ExceptionAiPdfRequestDto`。
  - `MyProject.Dtos/Admins/LogDtos.cs`：`LogSearchRequestDto`（快速區間 `LogQuickRanges`）、`LogEntryDto`、`LogSearchResultDto`、`LogViewerOptionsDto`、`LogAiReportPdfRequestDto`（Markdown、模型名稱、原因有長度上限）、`LogLevelStateDto`、`LogLevelApplyRequestDto`。
  - `MyProject.Dtos/Admins/TokenUsageDtos.cs`：`TokenUsageSearchRequestDto`、`TokenUsagePdfExportRequestDto`、`TokenUsagePurgeRequestDto`、`TokenUsageItemDto`、`TokenUsageTotalsDto`、`TokenUsageGroupRowDto`、`TokenUsageDailyRowDto`、`TokenUsageRangeCardDto`、`TokenUsageQuotaItemDto`、`TokenUsageDashboardDto`、`TokenUsageOptionsDto`、`TokenUsageFieldDto`、`TokenUsageDetailDto`。
  - `MyProject.Dtos/Admins/PromptTemplateDtos.cs`：`PromptTemplateDto`、`PromptTemplateVersionDto`、`PromptTemplateSaveRequestDto`、`PromptTemplateSaveResultDto`、`PromptTemplateActivateRequestDto`。
  - 畫面也要用的 AI 型別 0.10.5 起搬到 `MyProject.Web.Client/Ai/`（命名空間仍是 `MyProject.Web.Ai`）：`AiTokenUsage`（從 `MyProject.Web/Ai/AiModels.cs` 移出，新增 `FromDto`／`ToDto`、`ToDisplayText`）、`AiInterruption`、`AiMarkdownRenderer` 等；伺服器內部才用的型別仍留在 `AiModels.cs`。
- [x] integration tests 已驗證 Project CRUD 成功 response 可用 DTO contract 反序列化。
- [ ] 日後每新增一個 API Controller，都要在 PR checklist 檢查 DTO 邊界。

## 備註風險
- [ ] 目前 UI AdapterModel 與 API DTO 並存，命名與責任邊界需在 code review 中持續守住。