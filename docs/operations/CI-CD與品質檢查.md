# CI-CD 與品質檢查

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.4.32
- 首次實作版本：0.2.8
- 最後核對日期：2026/08/26

本專案以 **GitHub Actions** 在每次 push 與 PR 時自動建置、測試與品質檢查。工作流程定義於 [`.github/workflows/dotnet-ci.yml`](../../.github/workflows/dotnet-ci.yml)。

---

## 1. 觸發條件

| 事件 | 分支 |
|------|------|
| `push` | `main`、`codex/**` |
| `pull_request` | 目標為 `main` |

---

## 2. 工作流程（job：`build-test`）

執行環境：`windows-latest`，.NET SDK `10.0.x`。依序執行下列步驟，任一失敗即中止並讓 PR 無法合併：

| 步驟 | 指令 / 動作 | 目的 |
|------|-------------|------|
| Checkout | `actions/checkout@v6` | 取出原始碼 |
| Setup .NET | `actions/setup-dotnet@v5`（`10.0.x`） | 安裝 SDK |
| Restore | `dotnet restore src/MyProject/MyProject.slnx` | 還原相依套件 |
| Build | `dotnet build ... --configuration Release --no-restore` | Release 編譯（`TreatWarningsAsErrors`，任何警告即失敗）|
| Format check | `dotnet format ... --verify-no-changes --no-restore` | 依 `.editorconfig` 驗證格式，有差異即失敗 |
| Test | `dotnet test ... --configuration Release --no-build --verbosity normal` | 執行 xUnit 測試（見 [測試指南](../guides/測試指南.md)） |
| Documentation encoding check | `./scripts/Test-DocsEncoding.ps1`（pwsh） | 檢查 `docs/` 文件編碼 |
| Vulnerability scan | `dotnet list ... package --vulnerable --include-transitive` | 掃描已知弱點套件 |

---

## 2.1 建置設定與品質防線（0.4.32 起）⚠️

品質關卡由四個檔案構成，**新增專案或升級套件時請一律改這裡，不要回頭寫進個別 `.csproj`**：

| 檔案 | 作用 |
|------|------|
| [`.editorconfig`](../../.editorconfig)（repo 根目錄）| 程式碼格式規則，供 `dotnet format` 與 IDE 依循，並由 CI 的 Format check 強制。 |
| [`src/MyProject/Directory.Build.props`](../../src/MyProject/Directory.Build.props) | 全方案共用建置屬性：`Nullable`、`ImplicitUsings`、**`TreatWarningsAsErrors`**，以及 CVE 抑制。 |
| [`src/MyProject/Directory.Packages.props`](../../src/MyProject/Directory.Packages.props) | Central Package Management：所有套件版本的單一來源。 |
| [`global.json`](../../global.json)（repo 根目錄）| 鎖定 .NET SDK 版本（`10.0.400` + `rollForward: latestFeature`）。 |

幾個要點：

- **`TreatWarningsAsErrors` = true**：專案長期維持 0 warning，此設定是為了鎖住這個成果、避免警告悄悄回流。
  需要豁免時請針對**單一規則碼**加 `NoWarn` 並註明原因與解除條件（比照 `NuGetAuditSuppress` 的寫法），**不要整包關閉**。
- **各 `.csproj` 不再寫 `Nullable` / `ImplicitUsings` / 套件 `Version`**，只保留自己特有的設定
  （`TargetFramework`、`UserSecretsId`、`IsPackable` 等）。
- **`.editorconfig` 的定位是「描述現有慣例」**，不是引入新風格重新格式化整個 repo。
  因此刻意**不強制** namespace 宣告形式、`this.` 前綴、`var` 用法、檔案 BOM 與 using 排序
  —— 這些在專案中兩種寫法並存，強制會產生大量與需求無關的異動。

本機可先自行執行：

```powershell
dotnet build src/MyProject/MyProject.slnx -c Release
dotnet format src/MyProject/MyProject.slnx --verify-no-changes
```

---

## 3. 文件編碼檢查 ⚠️

`scripts/Test-DocsEncoding.ps1` 會**遞迴**掃描 `docs/` 下所有 `.md`，逐檔驗證：

- **必須含 UTF-8 BOM**（檔頭 `EF BB BF`），缺少即失敗。
- **不得含取代字元**（`U+FFFD`），出現代表編碼轉換時已產生亂碼。

> 注意：檔案移入子目錄後，此腳本以 `-Recurse` 涵蓋所有層級。以 PowerShell 建立／另存文件時請使用含 BOM 的 UTF-8（例如 `Set-Content -Encoding utf8BOM`），避免被擋下。編碼規定詳見 [維護規範 §3](維護規範.md)。

本機可先自行執行：

```powershell
pwsh ./scripts/Test-DocsEncoding.ps1
```

---

## 4. 弱點掃描

`dotnet list package --vulnerable --include-transitive` 會列出含已知弱點的直接與遞移相依套件。為避免大型還原逾時，步驟設定環境變數 `NUGET_HTTP_TIMEOUT_SECONDS=180`。發現弱點時應升級對應套件版本。

> 此步驟僅「列出」弱點，指令回傳 0、**不會讓 CI 失敗**；它與 restore/build 階段的 `NU1903` 稽核警告是兩條獨立路徑。

### 4.1 已知並已抑制的弱點：CVE-2025-6965

| 項目 | 內容 |
|------|------|
| 套件 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（bundled SQLite < 3.50.2） |
| Advisory | [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) / CVE-2025-6965（High，CVSS 7.2） |
| 引入來源 | 由 `Microsoft.EntityFrameworkCore.Sqlite 10.0.5` **遞移**引入（EF Core Sqlite → Microsoft.Data.Sqlite.Core → SQLitePCLRaw.bundle_e_sqlite3 → lib.e_sqlite3） |
| 為何不升級 | NuGet 上 `SQLitePCLRaw.*` 最新即 2.1.11，**尚無修補版**（無 2.1.12 / 2.2.x），EF Core 亦未帶入新版，目前無從升級 |
| 風險評估 | 低：EF Core 採參數化查詢，無未受信任的原始 SQL 進入 SQLite（0.4.24 起 SQLite 為唯一支援的資料庫） |
| 處置 | 於 [`src/MyProject/Directory.Build.props`](../../src/MyProject/Directory.Build.props) 以 `NuGetAuditSuppress` 抑制該 advisory，消除 restore/build 的 `NU1903` 警告 |

**重要行為差異**：`NuGetAuditSuppress` 只抑制 **restore/build 的 `NU1903` 警告**；上方的 `dotnet list package --vulnerable` 步驟為獨立查詢，**仍會列出**此 advisory（屬資訊性輸出，指令仍回傳 0、不阻斷 CI）。

**移除條件**：待 `SQLitePCLRaw`（或 `Microsoft.EntityFrameworkCore.Sqlite`）釋出 bundled SQLite ≥ 3.50.2 的版本後，升級套件、移除 `Directory.Build.props` 內的 `NuGetAuditSuppress`、並刪除本小節。

### 4.2 已解決：GHSA-v5pm-xwqc-g5wc（0.4.32）

| 項目 | 內容 |
|------|------|
| 套件 | `Microsoft.OpenApi` 2.4.1（受影響範圍 `>= 2.0.0-preview.11, <= 2.7.4`，修補版 **2.7.5**）|
| Advisory | [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc)（High）—— 循環 schema 參考可導致 OpenAPI 解析中止 |
| 引入來源 | 由 `Swashbuckle.AspNetCore` 10.1.5 → `Swashbuckle.AspNetCore.Swagger` **遞移**引入 |
| 處置 | **升級 `Swashbuckle.AspNetCore` 10.1.5 → 10.2.3**。10.2.1 起其相依改為 `Microsoft.OpenApi >= 2.7.5`，弱點自然消失，**不需要抑制、也不需要 pin 遞移套件** |

> 這是導入 `TreatWarningsAsErrors` 時才浮現的：該弱點以 `NU1903` 警告形式存在，
> 原本不會讓建置失敗，因此在 0.4.31 之前一直沒被注意到。

---

## 5. 延伸閱讀

- [測試指南](../guides/測試指南.md) — 測試類別、本機執行與覆蓋率。
- [維護規範](維護規範.md) — 版本 bump、文件同步與 commit 前檢查清單。
- [正式部署與安全檢查清單](正式部署與安全檢查清單.md) — 上線前必查項目。
