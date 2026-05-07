# Login 頁面改版紀錄

## 變更目的
將 `/Auths/Login` 從簡單表單升級為具備玻璃擬態、漸層動畫與企業商務風格的登入頁面，並補齊登入體驗所需的記住我與驗證碼功能。

## 本次修改檔案
- `src/MyProject/MyProject.Web/Components/Auths/Login.razor`
- `src/MyProject/MyProject.Web/Components/Auths/Login.razor.cs`
- `src/MyProject/MyProject.Web/Components/Auths/Login.razor.css`
- `readme.md`
- `docs/login-redesign.md`

## UI 調整
- 重新設計登入頁整體版面，加入品牌展示區與登入卡片雙欄配置。
- 使用純 CSS 實作 glassmorphism 毛玻璃卡片與動態漸層背景。
- 加入綠色品牌 Logo，採 inline SVG 實作，不依賴額外圖片檔案。
- 新增響應式設計，支援桌面、平板與手機瀏覽。
- 第二輪優化移除左側品牌區塊的額外說明卡片，讓版面更乾淨。
- 第二輪優化提高帳號、密碼、驗證碼輸入框的背景、邊框與聚焦狀態對比。
- 第二輪優化調整驗證碼顯示區的底色、字色與字距，提升辨識度。

## 功能調整
- 新增「記住我」勾選框，綁定 Cookie 驗證的持久化設定。
- 新增 4 位數驗證碼輸入與比對邏輯。
- 驗證碼錯誤或登入失敗時，會重新產生新的驗證碼。

## 編碼要求
- 本次新增與更新檔案皆以 UTF-8 編碼保存。
- 內容使用繁體中文撰寫，避免產生亂碼問題。
