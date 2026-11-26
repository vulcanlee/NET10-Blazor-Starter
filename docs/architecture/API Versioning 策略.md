# API Versioning 策略

- 文件版本：1.11
- 文件狀態：已實作
- 現行系統版本：0.10.43
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/26

## 目標
- [x] 建立版本化策略：所有 API 一律掛在 `/api/v1/...`。0.1.61～0.10.42 的過渡期另外保留沒有版本號的 `/api/...` 相容路由，0.10.43 起移除（前端、e2e 腳本與整合測試都只用 `api/v1`）。

## 現況
- [x] 資源 CRUD 控制器（`CategoryController`、`TeamController`、`ProjectController`）與 `AuthController` 只提供 `[Route("api/v1/[controller]")]`（0.10.43 起拿掉沒有版本號的舊路由 `api/[controller]`：前端、e2e 腳本都只用 `api/v1`）；
  附件下載原本的 `ProjectFileController`（`api/project-files`、`api/v1/project-files`）0.10.3 起已刪除，改用一次性下載票證。
  系統備份下載原本的 `BackupController`（`api/backups`，沒有 `v1`）0.10.6 起已刪除，改用一次性下載票證；`ExternalAuthController` 是 `Auths/Google` 網頁導向端點，不是 API。
  `New-CrudModule.ps1` 產生的 Controller 樣板同樣只有 `api/v1`（0.10.43 起）。
  WASM 遷移期間新增、給 WASM 頁面用的控制器（`ShellController`、`NotificationsController`、`AnnouncementsController`、`ProfileController`，0.10.2 起再加 `AuditLogsController`，0.10.3 起再加以票證下載的 `DownloadsController`，0.10.4 起再加只限管理員的 `UsersController`（`api/v1/users`）、`RolesController`（`api/v1/roles`）、`EmailLogsController`（`api/v1/email-logs`），0.10.5 起再加只限管理員的 `ExceptionLogsController`（`api/v1/exception-logs`）、`LogsController`（`api/v1/logs`）、`LogLevelController`（`api/v1/log-level`）、`AiCallLogsController`（`api/v1/ai-call-logs`）、`TokenUsageController`（`api/v1/token-usage`）、`PromptTemplatesController`（`api/v1/prompt-templates`），0.10.6 起再加只限管理員的 `ScheduledJobsController`（`api/v1/scheduled-jobs`）、`SystemHealthController`（`api/v1/system-health`）、`BackupsController`（`api/v1/backups`）、`DataRetentionController`（`api/v1/data-retention`）、`DatabaseUsageController`（`api/v1/database-usage`）、`SystemParametersController`（`api/v1/system-parameters`），以及首頁的 `DashboardController`（`api/v1/dashboard`），0.10.7 起再加只限管理員的 `AdminAssistantController`（`api/v1/admin-assistant`）（同時加入的 `AgentPlaygroundController`、`AgentWorkflowsController` 0.10.22 起已移除），`NotificationsController` 另加推播串流 `POST api/v1/notifications/stream`，0.10.8 起再加網頁登入與帳號自助的 `WebAuthController`（`api/v1/Auth`：`login-settings`、`login/web`、`two-factor`、`google/exchange`、`session/fork`）、`AuthPasswordController`（`api/v1/Auth/password`）、`TwoFactorSetupController`（`api/v1/Auth/two-factor/setup`））只有 `api/v1/...`。
  `AuthController` 的 `POST Auth/bridge`（0.10.0～0.10.7，網頁 Cookie 換 Token）0.10.8 起已刪除；App 用的 `POST api/v1/Auth/login`（同一個請求帶 `TwoFactorCode`）契約不變。
- [x] 整合測試以 `/api/v1/...` 驗證各 API 的 contract（0.10.43 起舊路由的測試改打 `api/v1`）。
- [x] Swagger UI 已設定 v1 文件入口；未導入額外 API Versioning 套件。

## 後續導入策略
- [x] 第一階段保留 `/api/...`，新增 `/api/v1/...` 平行路由（0.1.61～0.10.42）。
- [x] Swagger 分組顯示 `v1`，讓新用戶端從版本化路由開始串接。
- [x] 移除 `/api/...` 相容路由（0.10.43，使用者 2026/11/26 決定；前端與腳本都沒有在用，只有外部呼叫者可能受影響）。
- [ ] 所有 versioned endpoint 仍固定回傳 `ApiResult<T>`，並維持 DTO 邊界。
- [ ] 日後真的有破壞性變更時再新增 `/api/v2/...`，`v1` 依公告期保留。

## 驗收標準
- [x] `/api/v1/...` 與 Swagger 分組可正常運作。
- [x] 整合測試覆蓋 `/api/v1/...` 的主要 API contract（專案沒有 CI，提交前於本機品質閘門執行）。

## 備註風險
- [x] 不導入 API Versioning 套件是刻意決策。
- [x] 0.10.43 起沒有版本號的 `/api/Auth/login` 等舊路由回 404：仍在用舊路由的外部用戶端要改成 `/api/v1/...`（部署前請確認）。
