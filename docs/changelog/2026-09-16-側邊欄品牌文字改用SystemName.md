# 側邊欄品牌文字改用 SystemName，不再硬編（0.9.13）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.9.13
- 首次實作版本：0.9.13
- 最後核對日期：2026/09/16

## 起因

側邊欄左上的品牌文字一直是**硬編**的 `MyProject.Web`
（`Components/Layout/NavMenu.razor:17`），**不走**
`SystemSettings:SystemInformation:SystemName`。

這是文件早就標記出來、但一直沒修的陷阱：

- 上手指南 §7.6「仍然寫死、需要自行決定的文案」把它列為 ⚠️ 項目。
- §8.3「高風險清單」有**兩列**在講它：改了 `appsettings.json` 也不會變；
  更名腳本只會換成 `新代號.Web`，當作產品名仍然不對。
- §9 更名後驗證清單還要人工勾一條「側邊欄品牌文字已是新系統名」。

對**腳手架**而言這特別礙事：每個複製出去的新專案都得記得手改這一處，
忘了就會在側邊欄掛著別人的專案代號。

## 變更內容

| 檔案 | 異動 |
|------|------|
| `Components/Layout/NavMenu.razor:17` | `MyProject.Web` → `@SystemName`，並加上 `title="@SystemName"` |
| `Components/Layout/NavMenu.razor.cs` | 注入 `IOptions<SystemSettings>`，比照 `SplashView.razor.cs` 與 `MainLayout.razor.cs` 的既有寫法 |
| `Components/Layout/NavMenu.razor.css` | `.navbar-brand` 加上刪節號處理（理由見下節） |
| `appsettings.json` | `SystemVersion` 0.9.12 → 0.9.13 |

`SystemName` 至此才真正是單一來源：**啟動頁、登入頁、登入後首頁、側邊欄、「關於」對話窗**五處。

### 順帶處理：長名稱的收尾方式

品牌文字改讀設定後，長度就不再由程式碼控制。實測 15 字的名稱時，
文字會被側邊欄邊界**靜靜切掉、沒有任何提示**（側邊欄展開寬度固定 300px）。

因此 `.navbar-brand` 加上 `overflow: hidden` ＋ `text-overflow: ellipsis` ＋ `white-space: nowrap`，
超出時以刪節號收尾；完整名稱放在 `title` 屬性，滑過即可看到。
上手指南 §7.4 的長度建議也補上了側邊欄的容納量（約 10 字）。

> 這不是原始需求的一部分，但「把文字改成可變的」本身就製造了這個破版可能，
> 屬於自己改動造成的後果，所以一併收乾淨。

## 文件同步（重點是「刪」不是「加」）

改完之後，文件裡那些「這裡是硬編、要自己改」的警告全部變成**錯誤資訊**，
留著會反過來誤導人，因此一併移除：

- §7.6 移除 `NavMenu.razor:17` 那一列（它不再是寫死文案）
- §8.3 移除**兩列**相關的高風險項
- §9 移除「側邊欄品牌文字已是新系統名」那條人工驗證步驟
- §7.1 產品名稱那列補上「側邊欄品牌文字」
- §7.4 由「四處同步生效」改為**五處**，長度建議補上側邊欄

改完全檔已無任何 `NavMenu.razor:17` 的殘留引用。

## 驗證

CI 四關全過：`dotnet build` 0 警告 0 錯誤、`dotnet format --verify-no-changes`、
`dotnet test` 485 passed、`Test-DocsEncoding.ps1`。

> 已確認沒有任何測試斷言在品牌文字上（`MyProject.Web` 在測試中的命中全是路徑字串）。

實跑（以 `support` 登入）：

| 項目 | 結果 |
|------|------|
| 側邊欄展開 | 顯示「企業管理平台」，不再是 `MyProject.Web` |
| 側邊欄收合（預設）| 品牌文字隱藏，版面不跑掉 |
| **改設定後重啟** | 把 `SystemName` 暫時改成 15 字的測試名稱，側邊欄**確實跟著變** —— 證明真的走設定 |
| 長名稱 | 以刪節號收尾（`測試用超長系統...`），`title` 屬性保有完整名稱，頁面無水平捲軸 |
| console | 零錯誤 |

驗證後 `SystemName` 已改回「企業管理平台」。

## 相關文件

- [VS Code 開發環境與新專案上手指南 §7](../guides/VS%20Code%20開發環境與新專案上手指南.md)（品牌客製化）
- [首頁與導覽 PRD](../prd/首頁與導覽-prd.md)
- [品牌客製化章節，`SystemDescription` 接上啟動頁與登入頁（0.9.2）](2026-08-27-品牌客製化與SystemDescription接上兩頁.md)
