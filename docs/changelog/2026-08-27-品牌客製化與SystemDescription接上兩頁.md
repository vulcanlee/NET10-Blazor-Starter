# 品牌客製化章節，`SystemDescription` 接上啟動頁與登入頁（0.9.2）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.2
- 首次實作版本：0.9.2
- 最後核對日期：2026/08/27

## 症狀

開發者用本腳手架開新系統時，「換成自己的門面」這件事有三個坑：

1. **改了 `SystemDescription` 卻沒反應。** 啟動頁與登入頁上看起來像「產品簡短說明」的那兩段文字，其實是**寫死在 `.razor` 裡**的字面值，`appsettings.json` 改再多次都不會變。`SystemDescription` 實際上只影響「關於」對話窗。
2. **換了 `brand-logo.png` 卻還是舊圖。** 兩處引用都是裸路徑 `/images/brand-logo.png`，沒有 fingerprint，瀏覽器會持續吐快取中的舊圖。同專案的 `favicon.png` 反而是正確的 `@Assets["favicon.png"]` 寫法。
3. **不知道要換哪些檔、規格是什麼。** 上手指南（0.9.1）只在更名步驟裡帶過一句「品牌圖檔是二進位檔，腳本不會動」，沒有尺寸、裁切行為與 favicon 產生方式。

附帶問題：`appsettings.json` 出廠值 `SystemDescription` 與 `SystemName` 一字不差（都是「企業管理平台」），「關於」對話窗因此重複顯示同一句。

## 根因

`0.4.25`（[品牌圖片與網站圖示更換](2026-08-25-品牌圖片與網站圖示更換.md)）把品牌名稱收斂成單一來源時，**只處理了 `SystemName`** —— 兩頁的 `<h1>` 改綁 `@SystemName`，但底下的說明段落與圖片路徑原封不動留在 `.razor` 裡。當時的變更紀錄寫「此後改名只需異動 `appsettings.json` 一處」，指的僅是名稱，未涵蓋說明文字。

| 位置 | 0.9.2 之前 |
|------|------|
| `Components/Auths/Login.razor:17` | `<p class="brand-description">以現代化企業風格打造的安全登入入口，結合玻璃擬態、動態漸層與清晰的登入流程。</p>` |
| `Components/Views/Commons/SplashView.razor:13-15` | `<p class="splash-description">以清楚的模組結構、權限控管與後台管理流程，協助團隊快速進入系統。</p>` |
| `Login.razor:14`、`SplashView.razor:6` | `src="/images/brand-logo.png"`（裸路徑，無 fingerprint） |

## 修正

### 一、`SystemDescription` 接上兩頁

兩支 code-behind 本來就已注入 `IOptions<SystemSettings>`，比照既有的 `SystemName` 各加一個計算屬性：

```csharp
private string SystemDescription => SystemSettingsOptions.Value.SystemInformation.SystemDescription;
```

- `Components/Auths/Login.razor.cs`、`Components/Views/Commons/SplashView.razor.cs` 各新增一個屬性。
- `Login.razor:17`、`SplashView.razor:13` 的寫死文字改為 `@SystemDescription`。

此後 `SystemName` 與 `SystemDescription` 都是單一來源，改 `appsettings.json` 一處即同步反映在**啟動頁、登入頁與「關於」對話窗**三處。

### 二、`brand-logo.png` 改用 `@Assets[...]`

`Login.razor:14` 與 `SplashView.razor:6` 由 `src="/images/brand-logo.png"` 改為 `src="@Assets["images/brand-logo.png"]"`，與 `App.razor:15` 的 favicon 寫法一致。實跑確認輸出為 `images/brand-logo.854k3jgc1o.png` —— 換檔後 URL 自動改變，開發者不需要清快取。

### 三、出廠的 `SystemDescription` 改成真正的說明

```json
"SystemName": "企業管理平台",
"SystemDescription": "以模組化結構、權限控管與後台管理流程，協助團隊快速建立企業內部系統。"
```

長度需同時適配登入頁（較窄，折兩行）與啟動頁（單行）兩處版面，實測皆無溢出。

### 四、上手指南新增第 7 章「品牌客製化」

[VS Code 開發環境與新專案上手指南](../guides/VS%20Code%20開發環境與新專案上手指南.md) 插入新的第 7 章，原 §7～§10 順延為 §8～§11（目錄與所有內部錨點同步更新）。新章節含 7 個小節：

- **7.1** 總表：四樣東西分別改哪個檔、使用者會在哪裡看到（含 `SystemVersion` 在「系統健康監控」頁的第 4 個顯示位置）。
- **7.2** `brand-logo.png`：1024×1024、`object-fit: cover` 會裁成正方形、登入頁實際顯示 108×108、啟動頁 120×120（行動版 96×96）。
- **7.3** `favicon.png`：64×64，附 ffmpeg 產生指令；說明本專案刻意沒有 `.ico` / apple-touch-icon / webmanifest。
- **7.4** 產品名稱與說明的建議長度與破版界線。
- **7.5** ⚠️ 瀏覽器分頁標題是獨立機制，`SystemName` 不參與；`Home.razor` / `HomeAuthed.razor` / `Error.razor` 三處仍是英文範本殘留。
- **7.6** 仍然寫死的 11 處設計文案清單（含 `NavMenu.razor:17` 不走 `SystemName` 這個陷阱）。
- **7.7** 換完的逐項確認清單。

§8.3 高風險清單另補兩列（二進位圖檔、NavMenu 產品名）；§8.1 與 §8.2 步驟 7 的品牌段落改為連結指向第 7 章，避免兩處日後不一致。

## 驗證

四道關卡：`dotnet build --no-incremental` 0 警告 0 錯誤、`dotnet format --verify-no-changes` 無差異、`dotnet test` 291/291 通過、`Test-DocsEncoding.ps1` 全數通過。

**實跑驗證**（Playwright 無頭瀏覽器 + `curl`，`https://localhost:7044`）：

| 檢查點 | 結果 |
|------|------|
| 啟動頁 `.splash-title` / `.splash-description` | `企業管理平台` / 新的 `SystemDescription` ✅ |
| 啟動頁 `.splash-brand-image` src | `images/brand-logo.854k3jgc1o.png`（已帶 fingerprint）✅ |
| 登入頁 `.app-title` / `.brand-description` | 同上兩值 ✅；`scrollHeight == clientHeight`，面板無溢出 ✅ |
| 「關於」對話窗 | 系統名稱／系統描述／系統版本 `0.9.2 (2026/08/27)`，描述不再與名稱重複 ✅ |
| 系統健康監控頁 `/system-health` | `環境：Development；版本：0.9.2 (2026/08/27)；…` ✅ |

> 啟動頁只在身分驗證那一瞬間存在（`SplashView.OnAfterRenderAsync` 完成檢查後即導向），驗證時以攔截 `/Auths/Login` 請求的方式讓它停留在畫面上取樣。

## 版本

`0.9.1 (2026/08/27)` → `0.9.2 (2026/08/27)`

> 返回 [變更紀錄索引](README.md)
