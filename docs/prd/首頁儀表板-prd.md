# 首頁儀表板 PRD

- 文件版本：1.3
- 文件狀態：已實作
- 現行系統版本：0.10.6
- 首次實作版本：0.9.106
- 最後核對日期：2026/11/11

## 一、目標與範圍

登入後首頁 `/App` 原本是靜態的系統介紹；改成「精簡歡迎列＋快速入口＋小工具區」，讓每個人一眼看到與自己有關的狀態，並提供衍生系統擴充小工具的框架（路線圖 D-14）。

使用者決定（2026/10/04）：

| 題目 | 決定 |
|---|---|
| 版面 | 保留精簡的歡迎列與快速入口，下方是小工具區 |
| 內建小工具 | 「我的通知」「排程作業狀態」（管理員）「我的帳號」；不做專案狀態統計 |

- **範圍**：小工具的登記與依權限過濾、每個小工具獨立的錯誤邊界、三個內建小工具。
- **非範圍**：使用者自訂排版（拖拉、隱藏、大小）、小工具設定、圖表元件（YAGNI：目前沒有實際需求，有需要時再加）。

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|---|---|---|---|
| `/App`（0.10.6 起 `MyProject.Web.Client/Pages/HomeAuthed.razor` → `Views/Commons/HomeWelcomeView`，WebAssembly） | `ClientMainLayout`（0.10.6 前 `MainLayout`） | 頁面鍵「首頁」 | 所有登入者 |
| `api/v1/dashboard`（0.10.6 起） | — | 頁面鍵「首頁」的 view（`scheduled-jobs` 端點 `[AdminOnly]`） | WASM 首頁 |

| 小工具 | 代號 | 順序 | 誰看得到 |
|---|---|---|---|
| 我的通知 | `notifications` | 10 | 所有人 |
| 我的帳號 | `account` | 20 | 所有人 |
| 排程作業 | `scheduled-jobs` | 30 | 管理員（`AdminOnly`） |

## 三、畫面與欄位

- **歡迎列**：品牌圖示（72px）、`Welcome`、系統名稱與說明（`ISystemIdentity`）、「系統版本 … · 執行環境 …」。0.9.105 之前的六張「系統能力」卡片與底部版本區塊移除。
- **快速入口**：專案項目／分類清單／團隊清單，依頁面權限過濾（不變）。
- **儀表板**（`DashboardGrid`）：自動排列的卡片（最小寬 18rem），每張有圖示與標題。
  - **我的通知**：未讀數、最新 5 則（未讀粗體、時間、站內連結點了標為已讀並前往）；沒有通知顯示「沒有通知。」；說明全部通知在右上角鈴鐺。
    在鈴鐺標為已讀、刪除或「清除已讀」時立即同步（0.9.122／0.9.125 起）；別的行程（IIS 重疊回收、多台主機）發的通知與鈴鐺一樣每 60 秒輪詢補上（0.9.122 起，之前要重新整理）。
    0.10.6 起（WASM）收不到伺服器訊號：每 60 秒與切回分頁時重讀；在小工具點開通知後經 `ClientNotificationSignal` 請鈴鐺與自己立即重讀；在鈴鐺標為已讀、刪除、全部已讀或清除已讀後，鈴鐺以 `RequestRefresh(except: 自己)` 請小工具立即重讀（鈴鐺自己已經重讀過，不再多打一次 API）。
  - **我的帳號**：上一次登入（時間＋「帳號密碼」／「Google 登入」＋來源 IP，0.9.123 起；沒有 IP 的舊紀錄不顯示）、密碼到期（7 天內加「快到期」）、兩步驟驗證「已啟用／未啟用」；沒有本機密碼的人不顯示後兩項；連到個人資料。
  - **排程作業**：總開關關閉時提醒；有異常的作業與原因（上次執行失敗、上次執行被中斷、逾期未執行、已停用）；都沒有時「正常」；連到排程作業頁。
  - 小工具出錯時那張卡片顯示「這個區塊暫時無法顯示，其他區塊不受影響（錯誤追蹤碼：…）」。

## 四、內部系統運作

0.10.6 起（WASM 遷移 M7）首頁在 WebAssembly 執行：**看得到什麼由伺服器決定，畫面只渲染收到的**。

- **登記（伺服器）**：`DashboardWidgetDescriptor(Id, Title, Order, PermissionKey?, AdminOnly, Icon)`；`services.AddDashboardWidget("代號", …)` 以 singleton 登記，`AddDashboard()` 登記三個內建小工具與 `DashboardWidgetCatalog`（代號重複時啟動失敗）。0.10.6 前描述子另有 `ComponentType`，登記是 `AddDashboardWidget<TComponent>`。
- **元件（Client）**：`MyProject.Web.Client/Views/Dashboard/DashboardWidgetComponents.All` 以代號對應元件；前端不認得的代號（伺服器較新）不顯示內容，不讓首頁壞掉。兩邊的代號由 `DashboardTests` 檢查一致。
- **過濾**：`GET api/v1/dashboard`（`DashboardController`）以 `DashboardWidgetCatalog.VisibleTo(isAdmin, hasPagePermission)` 過濾 —— 管理員專屬的只給管理員、有權限鍵的要有該頁權限（`PermissionRules.CanAccessPage`，管理員短路），依 `Order`、`Id` 排序；同一個回應帶系統名稱、說明、版本、環境與依權限過濾的快速入口（`DashboardQuickLinks`）。
  ⚠️ 只有看得到的小工具會被放進 `DashboardGrid`，沒權限的元件根本不建立、不呼叫資料端點；資料端點本身也有同樣的權限（排程作業 `[AdminOnly]`，一般使用者直接呼叫得 403）。
- **渲染**：`DashboardGrid` 對每個小工具用 `DynamicComponent`，各自包在 `ClientErrorBoundary`；小工具繼承 `DashboardWidgetBase`，**自己讀資料**，讀不到時呼叫 `OnFailed`，那張卡片顯示錯誤與 API 回應的追蹤碼。
- **資料端點**（不寫稽核，時間以不帶時差送出）：
  - 我的通知：`GET api/v1/dashboard/notifications` —— 未讀數與最新 5 則，連結經 `ReturnUrlGuard` 只留站內網址。畫面以 `PeriodicRefresh` 每 60 秒與切回分頁時重讀（與 WASM 鈴鐺相同），並訂閱 `ClientNotificationSignal`；點開通知標為已讀後呼叫 `RequestRefresh()`。⚠️ `Dispose` 時取消訂閱並停掉定期更新。
  - 我的帳號：`GET api/v1/dashboard/account` —— `ProfileService.GetAsync`（密碼到期，7 天內 `PasswordExpiresSoon`；兩步驟驗證）與 `GetPreviousLoginAsync`（`AuditLog` 以精確 `ActorUserId` 取 `Login.Success`／`Login.Sso.Success` 的倒數第二筆 —— 最近一筆通常就是這次登入）。
  - 排程作業：`GET api/v1/dashboard/scheduled-jobs`（`[AdminOnly]`）—— `ScheduledJobOverviewService.GetOverviewAsync` → `ScheduledJobAttention.From`（與排程作業頁、系統健康監控同一份總覽）。

### 新增一個小工具

1. 在 `MyProject.Web.Client/Views/Dashboard/` 寫一個元件（`.razor`，繼承 `DashboardWidgetBase`），登記到 `DashboardWidgetComponents.All`。資料經 `DashboardApi` 呼叫自己的端點，讀不到時呼叫 `FailAsync`；需要定期更新就用 `PeriodicRefresh` 並在 `Dispose` 停掉。
2. 在 `Web/Dashboard/DashboardWidgets.cs` 的 `AddDashboard()` 加一行 `services.AddDashboardWidget("代號", "標題", 順序, "classic Material Icons 名稱", permissionKey: MagicObjectHelper.角色_…, adminOnly: …)`（代號與第 1 步相同）。
3. 在 `DashboardController` 加資料端點，權限與登記一致（`[HasPermission(…)]` 或 `[AdminOnly]`），DTO 放 `MyProject.Dtos/Shell/DashboardDtos.cs`。
4. 樣式用 `DashboardGrid.razor.css` 既有的 `dashboard-*` 類別（顏色一律取 `theme.css` 色票）。
5. 補測試（比照 `DashboardTests`、`OpsDashboardApiTests`）並更新 `Datas/Help/app.md` 的「儀表板區塊」表。

## 五、權限與安全

- 每個小工具只顯示目前使用者自己的資料（以 `CurrentUserService.CurrentUser.Id` 查詢）。
- 小工具的可見性用登記的權限鍵判斷（0.10.6 起在伺服器判斷）；資料端點各自有權限標註，過濾不是唯一的安全邊界。
- 通知連結只導向站內網址（`ReturnUrlGuard`，與鈴鐺相同）。

## 六、錯誤與邊界

- 小工具讀取失敗：只有那張卡片顯示錯誤與追蹤碼（0.10.6 起是 API 回應的追蹤碼；渲染例外由 `ClientErrorBoundary` 回報伺服器），其他卡片與頁面照常。
- 只登入過一次：「上一次登入」顯示「（沒有紀錄）」。
- 排程總開關關閉：「排程總開關已關閉，所有作業都不會依排程執行。」。
- 沒有任何看得到的小工具：整個「儀表板」區段不顯示。

## 七、驗收與測試

- `MyProject.Tests/Components/DashboardTests.cs`（0.10.6 起改寫為 WASM 版，7 個）：過濾（管理員、權限鍵、管理員專屬）與排序、⭐ 內建三個小工具在伺服器與 Client 兩邊都有登記且排程作業只限管理員、⭐ 只渲染伺服器給的小工具且讀不到資料的只影響自己的卡片（bUnit）、我的通知（未讀數與項目、⭐ 被要求時重讀）、我的帳號顯示伺服器算好的內容、上一次登入的查詢（跳過最近一筆、忽略失敗與別人）、排程作業的異常規則。
- `MyProject.Tests/OpsDashboardApiTests.cs`（0.10.6 起）：管理員看得到全部小工具與快速入口、只有首頁與專案權限的人看不到管理員小工具與其他入口、0.10.17 起沒有首頁權限也能呼叫（首頁不受權限限制，排程作業小工具仍只限管理員）；我的帳號是上一次登入不是這次；我的通知最新 5 則且只留安全連結；排程小工具列出需要注意的作業。實機 `scripts/e2e/m07-ops.mjs` M7-P6（一般使用者沒有排程作業小工具、直接呼叫得 403）。
- 故意改壞 9 處全部被測試抓到（見 [changelog](../changelog/2026-10-04-首頁儀表板.md)）。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Dashboard/DashboardWidgets.cs`（登記、過濾、`AddDashboard`、快速入口 `DashboardQuickLinks`）、`ScheduledJobAttention.cs`、`Controllers/DashboardController.cs`（0.10.6 起）
- `src/MyProject/MyProject.Web.Client/Views/Dashboard/`（0.10.6 起）：`DashboardGrid.razor(.css)`、`DashboardWidgetComponents.cs`（代號→元件、`DashboardWidgetBase`）、`MyNotificationsWidget.razor(.cs)`、`MyAccountWidget.razor`、`ScheduledJobsWidget.razor`
- `src/MyProject/MyProject.Web.Client/Views/Commons/HomeWelcomeView.razor(.cs/.css)`、`Pages/HomeAuthed.razor`、`Api/OpsApi.cs`（`DashboardApi`）；DTO `MyProject.Dtos/Shell/DashboardDtos.cs`
- `src/MyProject/MyProject.Business/Services/DataAccess/ProfileService.cs`（`GetPreviousLoginAsync`）
- 說明頁：`Datas/Help/app.md`
- 交叉連結：[首頁與導覽](首頁與導覽-prd.md)、[站內通知與公告](站內通知與公告-prd.md)、[個人資料](個人資料-prd.md)、[排程作業](排程作業-prd.md)
