# 換電腦交接紀錄（暫時文件，用完即刪）

- 建立：2026/11/06，在公司電腦寫給家裡電腦。
- 原因：Claude Code 的記憶存在各台電腦的使用者目錄（`~/.claude/projects/...`），不會跟著 repo 走。這份把還沒做完的工作與工作慣例帶過去。
- 目前 main：`976924f`「合併：系統管理助理（里程碑 4 後續修正）（0.9.159 → 0.9.160）」，版本 **0.9.160**，2135 個測試、品質關卡全過。

## 給另一台電腦的 Claude：請先做這三件事

1. 讀完這份文件。
2. 把「三、工作慣例與偏好」與「二、未完成工作」的重點寫進你自己的記憶（`MEMORY.md` 與各記憶檔），之後就不必再讀這份。
3. 依「五、何時清理」在工作完成後刪掉這份文件、`CLAUDE.md` 裡指向它的那一行，以及相關記憶。**刪除前先問使用者。**

## 一、已完成（不用再做）

- MAF 全面遷移四階段：0.9.140～0.9.144。
- 系統管理助理，需求見 `docs/prd/系統管理助理-prd.md`，手冊見 `docs/features/系統管理助理.md`，每版都有 changelog：
  - M1（0.9.155）：單一入口與自動分派。
  - M2（0.9.157）：26 個查詢工具與巡檢。
  - M3（0.9.158）：22 個可復原的修改，要核准。
  - M4（0.9.159）：15 個無法復原的動作，紅字核准窗、要輸入「確認」。
  - M4 後續（0.9.160）：核准窗顯示「目前狀態」。
- M4 審查留下的 3 個小問題已在 0.9.160 處理完，目前沒有已知的待修問題。
- 第二版定時巡檢（原本的 A）：2026/11/07 在家裡電腦完成並合併（0.9.161），見 `docs/changelog/2026-11-07-系統管理助理定時巡檢.md`。

## 二、未完成工作

### B. 「軟體專案交接知識助理」（2026/10/20 選定的題目，尚未開發，下一個要做的）

- 方向：用本腳手架 × MAF 做「軟體專案交接知識助理」。裝在企業自己的機器，讀 git、文件、會議紀錄；接手的人問答時附出處；有交接盤點流程，需要時回頭問原開發者（human-in-the-loop）。目標是企業付費的被動收入。
- 已確認：
  - 客戶自帶 OpenAI／Azure OpenAI 金鑰。
  - 第一版問答與盤點兩面都做。
  - 反目標：太貴、看不到效益、要改變日常作法。
  - 第二候選題目「待辦追蹤代理」延後。
- 方向文件已放進 repo：`docs/prd/軟體專案交接知識助理-方向價值與開發計畫.md`（含里程碑 M0～M5）。開始實作時先讀第 6 章（落點、四個缺口、慣例）；MAF 需做 `IChatClient` 轉接層，以符合速查表 §6.7 的記帳紅線。

## 三、工作慣例與偏好（請寫進記憶）

- **一律用繁體中文**回覆、出選項與寫計畫。程式識別字與路徑維持原文。
- **不自動 commit**。使用者說「commit / merge / push / delete local branch」時才做：
  1. 在功能分支 commit，訊息結尾加 `Co-Authored-By`。
  2. `git checkout main`，再 `git pull --ff-only`。
  3. `git merge --no-ff`，合併訊息格式為「合併：{主題}（舊版 → 新版）」。
  4. `git push origin main`，最後刪掉功能分支。
- 每次有建置內容就把 `appsettings.json` 的 `SystemSettings.SystemInformation.SystemVersion` Patch +1（格式 `Major.Minor.Patch (YYYY/MM/DD)`），並寫進 commit 訊息。
- `docs/` 與根目錄的 `.md` 要 **UTF-8 含 BOM**。提交前跑 `pwsh ./scripts/Invoke-QualityGate.ps1`（7 步）。
- 新功能的做法：
  1. 先用 AskUserQuestion 釐清需求，再寫計畫。
  2. 開發時 TDD，先看到測試失敗。
  3. 要跑網站實機驗證。
  4. 合併前派一個獨立審查。
  5. 同步更新頁面說明、使用手冊（附實機截圖與測試報告）、PRD、架構文件、速查表、changelog、文件索引與測試數。
- **AI 相關修改要實際跑網站驗證**，不能只靠單元測試：
  - 啟動網站：`dotnet run --launch-profile https --no-launch-browser`（https://localhost:7144）。
  - 登入用 support 帳號，**密碼向使用者要，不要寫進任何檔案或記憶**。驗證碼答案在登入頁的隱藏欄位 `Input.CaptchaCode`。
  - 用無頭瀏覽器 gstack browse（`~/.claude/skills/gstack/browse/dist/browse`，家裡電腦要另外裝）：
    - 填 Blazor 的 TextArea 要用 `fill @ref` 再 `press Tab`。
    - 截圖用 `screenshot --viewport`，只能存在 Temp 或 cwd 下。
    - AntDesign 頁面換頁後可能殘留空的 modal-wrap 擋住點擊，先 reload。
    - 新增使用者會連跳「確認儲存」「確認團隊設定」兩個確認窗。
  - 網站執行中會鎖住建置輸出，要跑測試前先停網站。
- 開發資料庫在 `C:\temp\MyProject\DB\BackendDB.db`。用 Python 查時一律 `file:...?mode=ro` 唯讀開啟；路徑錯時 sqlite3 會默默建一個空檔。日誌在 `C:\temp\Logs\MyProject.Web\`，AI 呼叫原文在 `C:\temp\MyProject\AiCallLog\`。
- 系統管理助理的設計不變量：見 `docs/architecture/開發慣例與限制速查.md`「每輪規劃的 Agent」第 1～12 點、`docs/architecture/MAF-AI呼叫架構.md` §6.9。
  - 巡檢的時間範圍要用相對參數（hours、days），不要把目前時間放進提示詞，會讓快取失效。
- 2026/11/06 實測過：OpenAI Responses API（`store=false`）在同一段對話中逐輪減少工具仍會接受。換模型或 API 後若回 400，要改成累積工具聯集。

## 四、家裡電腦的環境準備

- .NET SDK 10（公司電腦是 10.0.403）。
- User Secrets（`UserSecretsId` = `83f6d54f-9f33-4cd9-a626-d4c05c996e5d`）要自己設定，**值不在 repo 裡**，要設的鍵如下（用 `dotnet user-secrets set "鍵" "值" --project src/MyProject/MyProject.Web`）：
  - `AiSettings:Provider`、`AiSettings:Endpoint`、`AiSettings:ApiKey`、`AiSettings:Model`、`AiSettings:TimeoutSeconds`
  - `BootstrapSettings:SupportAccount`、`SupportName`、`SupportEmail`、`SupportPassword`
  - `JwtSettings:SigningKey`、`Issuer`、`Audience`、`AccessTokenMinutes`、`RefreshTokenDays`、`ClockSkewMinutes`
  - `EmailSettings:Provider`、`Host`、`Port`、`Security`、`UserName`、`Password`、`FromAddress`、`FromName`、`PickupDirectory`、`PublicBaseUrl`、`TimeoutSeconds`
- 家裡的開發資料庫是新的（第一次啟動自動建立），公司電腦上的測試資料、對話與備份不會在那邊。
- 實機驗證若要用真的模型，要先取得使用者同意才用 User Secrets 裡的金鑰打 API，而且不印出金鑰。

## 五、何時清理（使用者要求：用不到就刪）

- 家裡電腦的 Claude 把三、四節寫進記憶後，這份文件就只剩二節的工作清單有用。
- **A（定時巡檢）完成並合併後**：
  - 刪掉二節的 A。
  - 刪掉記憶中「定時巡檢偏好」這類只為 A 存在的條目。
- **A 與 B 都不再需要這份時**：
  - 刪除 `HANDOFF.md`。
  - 刪除 `CLAUDE.md` 最上方指向它的那一行。
  - 隨下一次 commit 一起提交。
- 每次刪除前都先告訴使用者要刪什麼，得到同意再刪。
- 公司電腦上的記憶，等使用者回公司時再一起整理。
