# AGENTS.md

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

* 有建置內容（程式、設定、會隨網站發佈的檔案，例如 `Datas/Help` 的操作說明）才把 appsettings.json 內的版本編號**最後一碼（Patch）加 1**（例：`0.4.0 → 0.4.1`，不進位、不分異動性質），並且在 commit message 中說明版本編號的變更；只改 `docs/` 或根目錄文件不升版。

* 所有文件都要採用 UTF-8 繁體中文編碼，並且不能夠有亂碼存在（`docs/` 下與根目錄的 `.md` 須**含 BOM**；專案沒有 CI，提交前在本機跑 `pwsh ./scripts/Invoke-QualityGate.ps1`，它依序執行建置、格式、測試、文件編碼、CRUD 產生器錨點與弱點掃描）

* 需要 commit 的時候，不能自動去做 commit

* 每次有異動後，要確認相關文件也要進行更新

---

## 專案速查與文件入口（NET10-Blazor-Starter）

動手改本專案前，請先讀 **`docs/architecture/開發慣例與限制速查.md`**（相對 repo 根目錄）—— 集中列出設計慣例、不變量與踩雷點；完整文件索引見 `docs/README.md`。

**更新 AI 模型計費**（OpenAI 調價、新模型、模型 Deprecated、匯率）時，一律依 **`docs/operations/AI模型計費更新指南.md`** 執行：先輸出盤點表給使用者確認，確認後才改 `appsettings.json`。

最關鍵的不變量（違反會改壞功能或留下隱患）：
- 0.10.14 起是 **.NET 11**（`net11.0`、SDK 11.0.100；專案名稱沿用 NET10）：Blazor 的新分析器（BL0016／BL0012）、`ValidateAsync`、WASM 瘦身開關與 `.config/dotnet-ef.json` 見速查表 §6.33 第 116～125 點；dotnet-ef 全域工具要 11 版；對話窗只能用預設頁尾（第 124 點）。
- **畫面選項資料一致性**（0.10.15 起）：下拉選項**開窗時重讀**（每個 `OnAddAsync`／`OnEditAsync`／`OnCopyAsync` 第一行 `await LoadFormOptionsAsync()`，存檔失敗後再讀；只有查詢條件的頁面在「重新整理」時重讀），**存檔時伺服器以 `SelectedOptionGuard` 擋下新選的、已不存在或已停用的選項**（400）；`FormOptionsConventionTests` 守門，CRUD 產生器樣板照做。見速查表 §6.33 第 126～129 點。
- 0.10.16 起（.NET 11 全面實機探索）：可搜尋的下拉（`EnableSearch`）每個 `SelectOption` 都要有 `Label`（否則打字比不到，`SearchableSelectConventionTests` 守門）；預期會發生的狀況不要靠 `catch (DbUpdateException)`（EF Core 會先記成 Error）；用戶端中斷的請求回 499、不算系統錯誤。見速查表 §6.33 第 130～135 點。
- 0.10.17 起**首頁不受權限限制**（`PermissionRules.OpenToEveryone`，角色矩陣沒有「首頁」）；清單搜尋框按 Enter 就搜尋；專案標題上限 200 字；帳號只能用英數字與 `- _ . @`（只在新增或改帳號時檢查）。見速查表 §6.33 第 136～140 點。
- 本系統**只支援 SQLite**；模型變更要在 `MyProject.AccessDatas/Migrations/` 產生 migration（0.4.24 起已移除 SQL Server 軌道）。
- 分層依賴一律向上：Web → Business → AccessDatas；`Share` 不相依其他專案，`Models`/`Dtos` 只相依 `Share`；UI 不直接 `using BackendDBContext`。
  0.10.0 起進行 WASM 改寫（計畫與各里程碑測試報告在 `docs/migration/`）：WASM 前端 `MyProject.Web.Client` 只能參考 Dtos／Models／Share；
  0.10.9 起已移除 Blazor Server 模式；0.10.10 起前端是獨立的 WASM 靜態站台（入口 `MyProject.Web.Client/wwwroot/index.html`）：畫面一律放 `MyProject.Web.Client`，
  `MyProject.Web` 只剩 API（`ClientSettings:BaseUrl` 留空＝同站台時另提供前端靜態檔；有值＝雙站台）；前端連伺服器的網址一律經 `ApiAddress`（`ApiOnlyHostTests`／`SameSiteHostTests`／`DualSiteHostTests` 守門）；
  刷新 Token 一律經 `IRefreshTokenStore`（落庫、輪替、重用撤銷）；API 的目前使用者由 `ApiActorMiddleware`＋`ICurrentUserLoader` 載入（唯一的載入規則）；
  測試讀原始碼一律經 `SourceTree`。見速查表 §1、§6.20、§6.33。
- Web API 一律回傳 `ApiResult<T>`，分頁包 `PagedResult<T>`；UI（WASM）與 API 一律用 JWT Bearer（0.10.8 起純 Token 登入，不再發登入 Cookie）。受保護 CRUD 另以 `[HasPermission("resource:action")]` + `IPermissionChecker` 做動作級授權（無權限回 403，維持 `ApiResult`；管理員短路）；UI 與 API 共用此單一 RBAC 權威來源。
- Blazor 檢視編輯前 `Clone()`。**不再需要手動清除 EF 追蹤** —— 0.4.36 起 Blazor 路徑的資料服務
  改注入 `IDbContextFactory<BackendDBContext>`，每個方法用完即棄，`CleanTrackingHelper` 已移除。
- 新增頁面權限採**宣告式**：`Menu.json`（每項唯一 `id`）＋ `SidebarMenuService.MenuPermissionMap`（`id→權限鍵`）＋ `MagicObjectHelper` 權限鍵常數；以 id 對應、重排 `Menu.json` 不錯位（舊「位置索引三處同步」已移除）。
- 單一版本來源 `SystemSettings.SystemInformation.SystemVersion`（有建置內容〔程式、設定、會隨網站發佈的檔案，例如 `Datas/Help` 的操作說明〕才 Patch +1，例：`0.4.0 → 0.4.1`；只改 `docs/` 或根目錄文件不升版；格式 `Major.Minor.Patch (YYYY/MM/DD)`）。
- **全站色票的唯一來源是 `src/MyProject/MyProject.Web.Client/wwwroot/theme.css` 的 `:root`**；`--ov-*`（浮層）與
  `--login-*`（登入頁）只是別名層。樣式要寫哪裡的判準是 **DOM 位置**不是元件名稱：AntDesign 渲染在
  `AntContainer` 底下的浮層 → `OverlayStyles.razor`；渲染在頁面內的（Table／Pagination／Input／Tag）→
  `theme.css`。見速查表 §6.9 與 `docs/architecture/介面視覺設計規範.md`。由 `ThemeConventionTests` 守門。
- **前端是 PWA**（0.10.11 起）：Service Worker（`MyProject.Web.Client/wwwroot/service-worker.published.js`）只在安裝時快取前端檔案、絕不快取 API 或資料；
  **新增不在 `/api/` 底下的伺服器端點（Controller 路由、OAuth 回呼、健康檢查等）時要加進 `networkOnlyPaths`**，
  否則同站台時導覽會被回成快取的 index.html（`PwaConventionTests` 守門）；`manifest.webmanifest` 的名稱與顏色是固定值，改 `SystemName` 或 theme.css 主色時一起改（0.10.13 起首次載入卡片的系統名稱也是固定值：`index.html` 與 `Commons/ClientLoadingScreen.razor`，一起改）。見速查表 §6.33。
- **每個登入後頁面都有「操作說明」**：新增 `@page` 必須登記 `MyProject.Web/Datas/HelpTopics.json` 並寫
  `Datas/Help/<路由>.md`（UTF-8 含 BOM、固定七段），或列入 `PageHelpCatalogTests.RoutesWithoutHelp`；
  並在 `docs/guides/新手完整操作指南.md` 新增標題含 `` `/路由` `` 的章節（`UserManualCoverageTests` 守門）；
  改了頁面的按鈕／欄位要同步改說明第三段，手冊也要同步。見速查表 §6.14。
- `docs/*.md` 與根目錄 `*.md` 須 UTF-8 **含 BOM**（`scripts/Test-DocsEncoding.ps1` 檢查，已含在 `Invoke-QualityGate.ps1` 內）。
