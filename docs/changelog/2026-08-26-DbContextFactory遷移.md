# Blazor 路徑改用 IDbContextFactory，CleanTrackingHelper 退場（0.4.36）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.42
- 首次實作版本：0.4.36
- 最後核對日期：2026/08/26

## 目的

`BackendDBContext` 原本以 `AddDbContext(..., ServiceLifetime.Scoped)` 註冊。

**在 Blazor Server，DI scope ＝ SignalR circuit** —— 存活時間等同使用者整個連線
（數分鐘到數小時），不是一次 HTTP 請求。後果：

1. 每位使用者一個 DbContext，追蹤的實體只增不減 → 記憶體成長、讀到過期資料。
2. 兩個元件事件重疊時會拋 `A second operation was started on this context`。
3. **`CleanTrackingHelper` 這條慣例本身就是這個問題的補丁** —— 全專案 **47 處**呼叫，
   散落 5 個服務（`MyUserService` 14、`CategoryService` 9、`TeamService` 9、
   `RoleViewService` 9、`ProjectService` 5）。每個新模組都得記得照抄，忘記就出錯。

微軟對 Blazor Server 的建議是 `AddDbContextFactory` + 每次操作用完即棄。
改掉之後，上述三個問題連同那條易忘的不變量會一起消失 ——
這是本輪改善中**唯一能同時消除一整類 bug 與一條慣例**的改動。

## 變更範圍

### 一、註冊方式

```csharp
services.AddDbContextFactory<BackendDBContext>(options => options.UseSqlite(...));
services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContext());
```

以工廠為主；仍保留一個 scoped `BackendDBContext`，但**改由工廠產生**
（避免 `AddDbContext` 與 `AddDbContextFactory` 的 options 生命週期衝突）。
它供 Repository（API 路徑，scope ＝ 單次 HTTP 請求，本來就正確）、
健康檢查與診斷服務使用。

### 二、五個資料服務改注入工廠

`Services/DataAccess/` 下的 `CategoryService` / `TeamService` / `RoleViewService` /
`ProjectService` / `MyUserService` 全部改為注入 `IDbContextFactory<BackendDBContext>`，
每個公開方法開頭：

```csharp
await using var context = await contextFactory.CreateDbContextAsync();
```

**遷移順序由簡到繁，每一步測試綠燈才進下一步**：
Category → Team → RoleView → MyUser → Project。

⚠️ **私有輔助方法若參與同一個工作單元，必須沿用呼叫端的 context**，不可自行建立：

| 服務 | 私有方法 | 處置 |
|------|----------|------|
| `ProjectService` | `SaveNewFilesAsync`、`RemoveProjectFilesAsync` | 加上 `BackendDBContext context` 參數 |
| `MyUserService` | `SyncAssignmentsAsync` | 同上（並改為 static） |

若讓它們各自建立 context，附件的實體檔案落地與資料表紀錄就不再是同一次 `SaveChanges`，
會出現「檔案寫了、紀錄沒寫」這類不一致。這是本次遷移**唯一不能機械化處理**的地方。

### 三、移除 `CleanTrackingHelper`

47 處呼叫全部消失後，`MyProject.Business/Helpers/CleanTrackingHelper.cs` 一併刪除。

### 四、測試

- 新增 `TestDbContextFactory`：在同一條 in-memory SQLite 連線上產生新 context，
  真實反映正式環境「每次操作各拿一個乾淨 context」的行為（而非整個測試共用一個）。
- 6 個測試 fixture 改用它建構服務。

## 驗證

`dotnet test` 由 244 → **250 個全數通過**。新增守門測試：

| 測試 | 驗證 |
|------|------|
| `DataAccessServiceLifetimeTests`（Theory ×5 + 1）| 五個服務的建構式**不得**出現 `BackendDBContext`、**必須**有 `IDbContextFactory<BackendDBContext>`；`CleanTrackingHelper` 不得回到 Business 組件 |
| `DataAccessServices_ShouldResolveFromContainer`（Theory ×5）| 從**真實 DI 容器**解析五個服務 |
| `DbContextFactory_ShouldResolveAndCreateContext` | 工廠可解析並建立 context |

> 後兩項是刻意補的：這些服務只被 Razor 元件使用，整合測試的 HTTP 端點碰不到它們。
> 若 DI 沒接好（例如漏了 `AddDbContextFactory`），單靠既有測試不會發現 ——
> 要到執行期開頁面才炸。

其餘關卡：0 warning、`dotnet format` 0 error、文件編碼檢查通過。

## 影響與注意事項

- **新增資料服務時一律注入 `IDbContextFactory<BackendDBContext>`**，
  不要注入 `BackendDBContext`（守門測試會擋）。
- **不再需要手動清除 EF 追蹤**。`CLAUDE.md` / `AGENTS.md` 的不變量清單、
  [開發慣例與限制速查 §3](../architecture/開發慣例與限制速查.md)、
  [架構總覽](../architecture/架構總覽.md)、
  [建立一個新 CRUD 操作網頁說明](../guides/建立一個新%20CRUD%20操作網頁說明.md) 皆已同步。
- **私有輔助方法要參與同一個工作單元時，把 `context` 當參數傳進去。**
- API 路徑的 Repository **維持** scoped context，不需要改。
