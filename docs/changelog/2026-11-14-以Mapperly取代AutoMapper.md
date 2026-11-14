# 以 Mapperly 取代 AutoMapper（0.10.23）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.10.23
- 首次實作版本：0.10.23
- 最後核對日期：2026/11/14

## 目的

腳手架會被複製成許多商用專案，所用的套件授權必須**完全免費、沒有使用條件**。AutoMapper 不符合：

- 15 版起改成商業授權（RPL-1.5 或付費），目前使用的 16.3.0 也是。
- 免費的 Community 授權只限年營收低於 500 萬美元的公司，政府機關不適用。
- 最後一個 MIT 版本 14.0.0 有高風險的阻斷服務漏洞 CVE-2026-32933（CVSS 7.5），修正只出在商業版（15.1.1／16.1.1）。

## 使用者決定（2026/11/14）

比較了三種免費做法，選定 **Mapperly**：

| 做法 | 授權 | 特色 | 結果 |
|---|---|---|---|
| **Mapperly** | Apache-2.0 | 編譯時產生對應程式碼、不用反射、最快；漏對應的欄位在建置時就失敗；持續維護（ABP 框架也從 AutoMapper 改用它） | **採用** |
| Mapster | MIT | 設定寫法最像 AutoMapper、改動最少；但執行時才產生對應，錯誤要執行時才發現；曾 2.5 年沒有發版 | 不採用 |
| 手寫對應 | 不需套件 | 零相依、和前端既有的 `XxxDtoMapper` 一致；但程式碼最多，加欄位要靠人記得改 | 不採用 |

## 變更內容

### 套件

- 移除 `AutoMapper`，加入 `Riok.Mapperly` 4.3.1（最新穩定版）。
- `MyProject.Business.csproj` 以 `ExcludeAssets="runtime" PrivateAssets="all"` 參考：它只在編譯時產生程式碼，執行時不需要任何組件。

### 對應類別

在 `MyProject.Business/Mappers/` 下，每個主題一個靜態類別：

- `ProjectMapper`、`CategoryMapper`、`TeamMapper`、`RoleViewMapper`、`MyUserMapper`
- `LogMappers`：Token 用量、AI 對話紀錄、系統例外、稽核、排程

方法一律是 `ToModel`（實體 → 畫面模型）、`ToModels`（清單）、`ToEntity`（畫面模型 → 實體），另有 `MyUserMapper.ToCurrentUser`。

- 服務不再注入 `IMapper`，改直接呼叫，例如 `CategoryMapper.ToModel(item)`。
- `Program.cs` 不必註冊任何東西。
- 每個類別都設定 `RequiredMappingStrategy.Target`，加上專案本來就開啟「警告視為錯誤」：畫面模型或實體的欄位如果沒有來源、也沒有明確忽略，**建置就失敗**（`RMG012`，指出是哪個欄位）。已實際拿掉一條忽略設定驗證過。

### 行為不變

- 分類與團隊的標籤字串和清單互轉，仍經 `TagStringHelper`。
- 「→ 實體」正規化名稱與代號，仍經 `NameNormalizer`。
- 角色預設團隊的 JSON 轉換，仍經 `TeamJsonHelper`。
- 計算欄位 `HasContent`、`HasLocalPassword` 照舊算出。
- 「→ 實體」不帶軟刪除欄位：原本的 `IgnoreSoftDeleteFields()` 改成每個方法標 `[MapperIgnoreTarget]`。
- 帳號的鎖定、失敗次數、密碼設定時間、安全戳記與兩步驟驗證欄位不寫回實體。
- 角色與附件等導覽屬性不寫回實體。服務原本就會清掉它們，結果相同。
- 稽核時間照原值搬運（UTC），換算成本地時間仍在 `AuditLogQueryService`。

### 移除

- `Business/Models/AutoMapping.cs`（原本唯一的 Profile）與 `SoftDeleteMappingExtensions`。
- 7 個沒有任何程式在用的對應：實體與 `ProjectDto`、`ProjectCreateUpdateDto`、`CategoryDto`、`CategoryCreateUpdateDto`、`TeamDto`、`TeamCreateUpdateDto` 之間的對應。Web API 與前端用的是手寫的 `XxxDtoMapper`，不受影響。
- 設定 `AutoMapper:LicenseKey`：`appsettings.json`、`Program.cs`、設定檔註解規範測試中都拿掉了，啟動時也不再出現授權警告。曾在 User Secrets 或環境變數 `AutoMapper__LicenseKey` 設過的值不會再被讀取，可以刪掉：

  ```
  dotnet user-secrets remove "AutoMapper:LicenseKey" --project src/MyProject/MyProject.Web
  ```

### CRUD 產生器

- 新增樣板 `scripts/crud-templates/Business/Mappers/__Name__Mapper.cs.tmpl`，`-WithTeams` 時另含標籤轉換。
- 服務與服務測試的樣板改用 `__Name__Mapper`。
- `New-CrudModule.ps1` 不再把對應插進 `AutoMapping.cs`（少一項登記與錨點），也不再產生沒人用的 Dto 對應。

## 測試

- 新增 `MapperTests`（12 個）：
  - 每個對應的行為：標籤、名稱與代號正規化、角色預設團隊、計算欄位、帳號安全欄位、導覽屬性、稽核時間。
  - 守門：每一個回傳軟刪除實體的對應方法，都以實際呼叫驗證不會帶入 `IsDeleted`、`DeletedAt`、`DeletedBy`，新增實體時忘了加會失敗。
- 約 35 支測試檔不再自己建立 AutoMapper 的設定。
- 測試總數 3,200 → 3,212。
- 實跑 CRUD 產生器驗證（`scripts/Test-CrudGenerator.ps1`）、既有 e2e 腳本，並實際複刻新專案驗證。

## 版本

0.10.22 → 0.10.23。
