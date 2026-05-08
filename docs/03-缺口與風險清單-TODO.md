# 缺口與風險清單 TODO

## 目標說明

- [ ] 將目前專案作為通用腳手架前必須補強的缺口集中管理。
- [ ] 將安全、套件、測試、CI、nullable warning、密碼與預設帳號風險整理成可追蹤待辦。
- [ ] 讓後續實作時可依風險優先順序逐項處理。

## 現況盤點

- [ ] `dotnet build src/MyProject/MyProject.slnx -v:minimal` 目前可建置成功。
- [ ] 以 `dotnet build src/MyProject/MyProject.slnx -v:minimal --no-incremental` 完整重建時，輸出顯示 `56 個警告`、`0 個錯誤`。
- [ ] 完整重建警告包含 `AutoMapper 16.1.0` 高風險弱點、nullable warning、migration 命名 warning、AntDesign Checkbox obsolete warning、Blazor form binding warning 與 `BuildServiceProvider` warning。
- [ ] `dotnet list src/MyProject/MyProject.slnx package --vulnerable --include-transitive` 顯示 `MyProject.Business` 直接引用 `AutoMapper 16.1.0` 有 High 嚴重性弱點。
- [ ] 同一弱點也透過 `MyProject.Business` 遞移影響 `MyProject.Web`。
- [ ] 目前沒有測試專案。
- [ ] 目前沒有 `.github/workflows` CI 設定。
- [ ] 目前 JWT 尚未實作，README 只列為待辦。
- [ ] 目前 API 只有檔案下載與 Weather 範例，尚無完整 CRUD API 樣板。
- [ ] 目前密碼使用自訂 SHA256 + salt，尚未使用 ASP.NET Core Identity 或 PBKDF2/BCrypt/Argon2 等密碼雜湊策略。
- [ ] 目前 `support` 預設帳號會在啟動時建立或更新，需明確標示正式環境風險。

## 實作待辦

- [ ] 升級或替換 `AutoMapper 16.1.0`，確認 GHSA-rvv3-g6hj-g44x 已解除。
- [ ] 執行 `dotnet list package --outdated`，建立套件升級清單。
- [ ] 新增測試專案，例如 `MyProject.Tests`，至少涵蓋 Business service、API result、JWT token service。
- [ ] 新增 GitHub Actions workflow，包含 restore、build、test、package vulnerability scan。
- [ ] 將 nullable warning 納入待辦，逐步修正 `CS8618`、`CS8603`、`CS8604`、`CS8625` 等問題。
- [ ] 修正 `Program.cs` 內 `BuildServiceProvider` 警告，避免建立額外 singleton 副本。
- [ ] 評估密碼雜湊策略，至少規劃 PBKDF2 或 ASP.NET Core Identity PasswordHasher。
- [ ] 將預設帳號、預設密碼、seed data 行為改成可設定、可停用、可在正式環境阻擋。
- [ ] 將完整例外回傳到 `ApiResult<T>.Exception` 的風險標示清楚，並在正式環境部署指南中特別警告。
- [ ] 為檔案上傳補副檔名白名單、MIME 檢查、檔名正規化、病毒掃描介面與下載授權檢查。
- [ ] 為公開 API 補 rate limiting 與 CORS 策略。
- [ ] 補健康檢查端點，例如 `/health/live`、`/health/ready`。

## 驗收標準

- [ ] 套件弱點掃描不再回報 High 或 Critical 弱點。
- [ ] `dotnet build` 在完整重建後沒有新增警告，且已知警告皆有追蹤項目。
- [ ] 測試專案可在本機與 CI 執行。
- [ ] CI 可在 pull request 自動檢查 build、test 與弱點掃描。
- [ ] 正式環境文件明確標示預設帳號、完整例外回傳、Swagger UI、JWT signing key 的安全注意事項。

## 相關檔案

- [ ] `src/MyProject/MyProject.Business/MyProject.Business.csproj`
- [ ] `src/MyProject/MyProject.Web/MyProject.Web.csproj`
- [ ] `src/MyProject/MyProject.Web/Program.cs`
- [ ] `src/MyProject/MyProject.Business/Helpers/PasswordHelper.cs`
- [ ] `src/MyProject/MyProject.Models/Systems/SystemSettings.cs`
- [ ] `.github/workflows/`

## 備註風險

- [ ] 完整例外資訊回傳給 API 用戶端雖方便除錯，但可能洩漏 stack trace、檔案路徑、資料庫資訊與內部類別名稱。
- [ ] 使用無狀態 refresh JWT 時，若 refresh token 外洩，在到期前無法可靠撤銷。
- [ ] 若未建立 CI 與測試，腳手架被多個未來系統複製後，問題會被同步複製。
