# CI-CD 與品質檢查

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
| Build | `dotnet build ... --configuration Release --no-restore` | Release 編譯 |
| Test | `dotnet test ... --configuration Release --no-build --verbosity normal` | 執行 xUnit 測試（見 [測試指南](../guides/測試指南.md)） |
| Documentation encoding check | `./scripts/Test-DocsEncoding.ps1`（pwsh） | 檢查 `docs/` 文件編碼 |
| Vulnerability scan | `dotnet list ... package --vulnerable --include-transitive` | 掃描已知弱點套件 |

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

---

## 5. 延伸閱讀

- [測試指南](../guides/測試指南.md) — 測試類別、本機執行與覆蓋率。
- [維護規範](維護規範.md) — 版本 bump、文件同步與 commit 前檢查清單。
- [正式部署與安全檢查清單](正式部署與安全檢查清單.md) — 上線前必查項目。
