# 預設密碼統一為 support、衍生專案重建 Migration、專案新增預設團隊（0.9.57）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.57
- 首次實作版本：0.9.57
- 最後核對日期：2026/09/24

## 為什麼

1. `appsettings.json` 出貨的 `SupportPassword` 是 `1qaz@WSX`，但 `BootstrapSettings` 類別預設值與整合測試用的都是 `support`，兩套值並存容易混淆。
2. `New-StarterProject.ps1` 產生的衍生專案會帶著腳手架全部的 migration 歷史，對新專案沒有意義。
3. 在「專案項目」新增紀錄時，團隊欄位是空白的，使用者每次都要自己挑團隊。

## 做了什麼

- **預設密碼**：`appsettings.json` 的 `BootstrapSettings:SupportPassword` 改為 `support`。`StartupSafetyValidator` 的範本密碼清單只留 `support`，`1qaz@WSX` 已移除；Production 仍會擋下留空與 `support`。相關文件（腳手架開發指引、上手指南、密碼種類與儲存機制、日誌與設定檔說明）同步更新，歷史 changelog 保留原樣。
- **New-StarterProject.ps1**：
  - 一開始就檢查 `dotnet ef` 是否可用，找不到會在複製之前中止。
  - 改名之後清空 `<新代號>.AccessDatas/Migrations/`（保留目錄），並刪除 `CategoryTeamUniqueIndexMigrationTests.cs`。
  - 先 `dotnet restore` 整個方案，再執行 `dotnet ef migrations add Init` 產生第一次 migration；任一步失敗都會中止，並印出可以手動重跑的指令。
  - 名稱用大寫 `Init`（與腳手架原本第一個 migration 的類別名相同）：全小寫 `init` 會觸發 CS8981，在 `TreatWarningsAsErrors` 下建置失敗。
- **專案項目**：新增時，團隊欄位預設帶入目前使用者的有效團隊，也就是直接綁定的團隊加上角色預設團隊（`IRecordAccessScopeProvider`）。只取目前啟用中、會出現在選項內的團隊。

## 關鍵決定

| 決定 | 理由 |
|---|---|
| 黑名單移除 `1qaz@WSX` | 出貨值已統一為 `support`，清單只需對應現行出貨值；`StartupSafetyConventionTests` 會讀實際出貨值守門 |
| dotnet-ef 缺少或 migration 失敗一律中止 | 不留下「看似完成、實則沒有 migration」的半成品 |
| 衍生專案刪除 `CategoryTeamUniqueIndexMigrationTests` | 它驗證的是腳手架自己的升級路徑，而且寫死了舊 migration 名稱，重建後必定失效 |
| 預設團隊過濾已停用團隊 | AntDesign 多選 Select 會把不在選項裡的值當成未知值 |
| 預設值在 `dirtyTracker.Capture` 之前設定 | 預設值不算使用者的變更，開窗後直接取消不會詢問 |

## 影響

- 已存在的資料庫下次啟動時，support 的密碼會被覆寫成 `support`（冪等 seed 的既有行為）。
- 管理員或沒有綁定任何團隊的使用者，團隊欄位維持空白，存檔時照舊跳出「公開給所有人」的確認。
