# appsettings.json 註解、正式環境標記與重新排序（0.9.176）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.176
- 首次實作版本：0.9.176
- 最後核對日期：2026/11/08

## 目的

`appsettings.json` 有 30 多個區段、上百個屬性，之前只能對照《日誌與設定檔說明》才知道每個值的用途、單位、範圍，也看不出哪些在正式環境一定要改。
這次在檔案裡直接寫說明，把正式環境要處理的鍵標出來，並把它們排到檔案最前面：打開檔案從上往下照做，看到「第二部分」就可以停。

## 變更內容

### 1. 註解

- **每個屬性行尾**一句 `//` 說明：用途、單位、範圍、特殊值（例如 0＝不清除、不限制）、可否在「系統參數」頁調整。
- **每個區段上方**一行標題（例如 `// ── 帳號鎖定 ──`）與 1～3 行注意事項：要不要重新啟動、寫錯會不會擋啟動、相關設定在哪個區段。
- **檔案開頭**一段圖例：兩種標記的意思，以及覆蓋順序（appsettings.json → appsettings.{環境}.json → User Secrets → 環境變數 → 「系統參數」頁）。
- `AiPricingSettings`：欄位意思寫在區段上方，只有第一個模型（`gpt-6-astra`）的欄位有行尾註解，其他 12 個模型欄位相同、不重複。
- **值、鍵名與順序完全沒有改**（去掉註解後和 0.9.175 逐一比對相同，只有版本號）。

### 2. 正式環境標記

| 標記 | 鍵 | 怎麼做 |
|---|---|---|
| `⚠️【正式必改】` | `SystemSettings:ExternalFileSystem` 的 12 個資料目錄、`NLog:BasePath`、`AllowedHosts`、`BootstrapSettings:SupportEmail`、`Swagger:EnabledInProduction`、`Security:ReturnExceptionDetails`、`Cors:AllowedOrigins`；有用到才要改：`EmailSettings` 的 `Provider`／`Host`／`FromAddress`／`PublicBaseUrl`、`AiSettings` 的 `Endpoint`／`Model`、`GoogleOAuthSettings` 的 `Enabled`／`ClientId` | 在 `appsettings.Production.json` 覆蓋 |
| `🔒【機密】` | `JwtSettings:SigningKey`、`BootstrapSettings:SupportPassword`、`AiSettings:ApiKey`、`EmailSettings` 的 `UserName`／`Password`、`GoogleOAuthSettings:ClientSecret`、`CacheSettings:RedisConnection`、`AutoMapper:LicenseKey` | 用環境變數提供（註解寫出名稱，例如 `JwtSettings__SigningKey`），不寫進任何 json 檔 |

`StartupSafetyValidator` 在正式環境會擋的鍵都在清單裡。`appsettings.Production.json` 沒有改（照使用者決定：只標記，不先放佔位值）。

### 3. 重新排序

| 位置 | 內容 |
|---|---|
| 檔案開頭 | 圖例（兩種標記、覆蓋順序、系統參數頁的優先順序） |
| 第一部分之一：一定要處理（不處理就不能上線） | `SystemSettings`、`NLog`、`AllowedHosts`、`BootstrapSettings`、`JwtSettings`、`Swagger`、`Security`、`Cors` |
| 第一部分之二：有用到才要處理 | `EmailSettings`、`AiSettings`、`GoogleOAuthSettings`、`CacheSettings`、`AutoMapper:LicenseKey` |
| 第二部分：一般設定（可以直接用預設值） | 其餘 25 個區段，維持原本的相對順序 |

- 區段內有標記的屬性排在最上面（`⚠️` 先、`🔒` 後），例如 `EmailSettings` 先列 `Provider`、`Host`、`FromAddress`、`PublicBaseUrl`、`UserName`、`Password`；`SystemSettings` 先列 `ExternalFileSystem`。
- 只改順序，鍵名與值不變：.NET 讀設定不看順序，「系統參數」頁的覆寫值、環境變數與 User Secrets 都不受影響。
- 原本合在一起的區段標題拆開（「API 限流」「允許的主機名稱」「AutoMapper 授權」各自一個標題）；「寄信佇列」的「連線設定在下方」改成「在第一部分」。

### 4. 讀檔端

- 測試：直接讀檔的 5 個測試類別（`AiSettingsTests`、`AiPricingSettingsTests`、`EmailSettingsTests`、`DocumentationConventionTests`、`StartupSafetyConventionTests`）改用共用的 `ShippedAppSettings.JsonOptions`（略過註解）；預設選項遇到註解會丟例外。
- `scripts/Restore-Backup.ps1`：新增 `Remove-JsonComments`，解析前先去掉 `//` 與 `/* */` 註解（字串裡的 `//` 與跳脫字元保留）。這支腳本規定用 Windows PowerShell 5.1 執行，5.1 的 `ConvertFrom-Json` 不接受註解。
- 網站執行期本來就略過註解，不受影響。

### 5. 新守門測試 `AppSettingsCommentConventionTests`

- 每個屬性都要有行尾註解（例外：只開一個物件的行、AI 計費第二個以後的模型）。
- 清單裡的鍵要有對應標記；標記只能出現在清單裡的鍵；機密鍵的註解要寫出 `區段__鍵` 環境變數名稱，出貨值只能是空字串或開發用佔位值。
- 註解不可寫出 `"SystemVersion":`（`scripts/New-StarterProject.ps1` 用 regex 取代版本號）。
- 「系統參數頁可改」要和 `SystemParameterCatalog.All` 完全一致（64 個鍵，不多不少）。
- 不可有結尾多餘的逗號（網站允許，但 Windows PowerShell 5.1 的 `ConvertFrom-Json` 不允許，還原腳本會讀不到路徑）。
- 排序：同一層裡，有標記的屬性（或底下有標記的物件）要排在沒有標記的前面（`MarkedSettings_ShouldComeFirst`；以重新排序前的檔案實測會失敗）。

## 新紅線

寫進[速查表 §6.32](../architecture/開發慣例與限制速查.md)：新增設定一定要加行尾註解並登記標記清單；有標記的設定放在第一部分；直接讀 appsettings 原文的程式必須接受註解；註解不可寫出 `"SystemVersion":`。

## 分組建議（本次不實作）

同一個需求的設定常分散在 2～3 個區段，例如稽核紀錄的記錄範圍（`AuditLogSettings`）、保留天數（`LogRetentionSettings`）、檔案位置（`ExternalFileSystem`）、排程（`ScheduledJobSettings:Jobs`）。
是否改成依功能分組由使用者決定；改鍵名會讓「系統參數」頁存在資料庫的覆寫值、主機的環境變數與 User Secrets 悄悄失效，需要資料移轉、舊鍵名相容與部署文件的改名對照，所以這次只加註解。

## 文件

- 日誌與設定檔說明 §4（註解與標記說明）、正式部署與安全檢查清單（照標記逐項處理）、開發慣例與限制速查（新增 §6.32）、腳手架開發指引（新增設定時要加註解）、測試指南、腳手架總覽與複刻上手、根目錄 readme。

## 驗證

- 去掉註解後與 0.9.175 的 appsettings.json 比對：值、鍵名與順序完全相同（只有版本號不同）。
- 測試 2492 → 2532 個（+40，`AppSettingsCommentConventionTests`），全部通過；新測試在加註解前先確認會失敗（35 個失敗），排序測試在重新排序前先確認會失敗。
- 重新排序後再比對一次：值與鍵名相同（只有順序不同）。
- 實際啟動網站（隔離所有路徑）：設定讀取正常、啟動完成，登入頁回應 200。
- `Restore-Backup.ps1` 的解析：Windows PowerShell 5.1 與 pwsh 7.6 都讀得到正確的路徑與版本號，字串裡的 `https://…//…` 與跳脫引號保留。
- 品質關卡 7 步通過（建置、格式、測試 2532 個、文件編碼、CRUD 產生器錨點、弱點掃描）。
- 獨立審查（以程式為準逐條核對註解）：
  - 高 3，與程式相反，已修：`UsdToTwd` 填 0 其實會讓網站起不來（不是「記為未定價」）；`AiSettings:TimeoutSeconds` 範圍是 1～3600、超出起不來（不是「退回 120 秒」）；`SQLiteDefaultConnection` 程式根本不讀。
  - 中 8，說法不精確，已修：推論等級空白是沿用 `AiSettings:AgentReasoningEffort`；support 已存在時只重設密碼並強制為管理員，姓名與 Email 不動；`ExpiryDays` 改了立即生效；計費的模型比對（回應裡的模型名稱、前綴比對）；系統參數頁存過值後設定檔不再生效；🔒 只有 SigningKey、SupportPassword 是一定要設；AI 設定不齊在任何環境都起不來；JWT 整個區段改了都要重啟。
  - 低：`/api` 登入限流不管畫面登入、Google 啟用時必填項、陣列逐項合併、Production.json 已設的三項註明「勿刪」、用詞統一、命令列參數的覆蓋順序等，已修。
  - 腳本與測試：`Restore-Backup.ps1` 遇到空檔回 null；測試補上「空註解不算」、系統參數頁對照、結尾逗號三項檢查。
  - 未處理：測試的標記清單沒有自動對照 `StartupSafetyValidator`（新增 Production 才擋的鍵時要手動加進清單，已寫進速查表 §6.32）；`EmailSettings:UserName` 標成機密屬判斷題，維持。
- 版本號 0.9.175 → 0.9.176。
