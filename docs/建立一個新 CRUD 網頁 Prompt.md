RoleViewView.razor 是一個標準的 CRUD 元件(可以做到新增、查詢、更新、刪除、過濾、排序)，可以用於建立一個 CRUD 的頁面
這裡需要建立一個 MyUser 的 CRUD 元件與頁面，參考 RoleViewView 元件，完美複刻 RoleViewView 元件的結構與功能和參考與引用的程式碼，並將其命名為 MyUserView.razor。
列出手動開發需要用到的計畫

我需要一個建立一個新 CRUD 操作網頁說明的計畫，請參考 RoleViewView.razor 是一個標準的 CRUD 元件(可以做到新增、查詢、更新、刪除、過濾、排序)，可以用於建立一個 CRUD 的頁面，此時對於所要做的相關工作與注意事項有哪些，才能完美複刻 RoleViewView 元件的結構與功能和參考與引用的程式碼。列出手動開發需要用到的計畫

我需要一個建立一個新 CRUD 操作網頁，請參考 RoleViewView.razor 是一個標準的 CRUD 元件(可以做到新增、查詢、更新、刪除、過濾、排序)，可以用於建立一個 CRUD 的頁面，要來完美複刻 RoleViewView 元件的結構與功能和參考與引用的程式碼。Entity 名稱為 Project 專案，欄位要有 Title , Description , StartDate , EndDate , Status , Priority , CompletionPercentage , Owner , CreatedAt , UpdatedAt 。 開始與結束日期可以為空值，狀態將會有：未開始、進行中、已完成、暫緩和等待選項，優先級將會有低、中、高三種選項，完成百分比為 0-100 的整數，負責人為一個使用者的名稱字串。資料輸入表單要使用 <FormModalHelper />，使其佔據螢幕 90% 寬與高。

我需要一個建立一個新 CRUD 操作網頁，請參考 RoleViewView.razor 是一個標準的 CRUD 元件(可以做到新增、查詢、更新、刪除、過濾、排序)，可以用於建立一個 CRUD 的頁面，要來完美複刻 RoleViewView 元件的結構與功能和參考與引用的程式碼。Entity 名稱為 MyTas 工作，欄位要有 Title , Description , StartDate , EndDate , Category , Status , Priority , CompletionPercentage , Owner , CreatedAt , UpdatedAt , ProjectId 。 開始與結束日期可以為空值，狀態將會有：未開始、進行中、已完成、暫緩和等待選項，優先級將會有低、中、高三種選項，完成百分比為 0-100 的整數，負責人為一個使用者的名稱字串。資料輸入表單要使用 <FormModalHelper />，使其佔據螢幕 90% 寬與高。

我需要一個建立一個新 CRUD 操作網頁，請參考 RoleViewView.razor 是一個標準的 CRUD 元件(可以做到新增、查詢、更新、刪除、過濾、排序)，可以用於建立一個 CRUD 的頁面，要來完美複刻 RoleViewView 元件的結構與功能和參考與引用的程式碼。Entity 名稱為 Meeting 會議，欄位要有 Title , Description , Summary , Participants , CreatedAt , UpdatedAt , ProjectId 。 開始與結束日期可以為空值。資料輸入表單要使用 <FormModalHelper />，使其佔據螢幕 90% 寬與高。

這個方案名稱為 MyProject ，在此方案內的所有專案都會有 MyProject 的前綴名稱，例如 MyProject.Web、MyProject.Business 等等。我想要將這個 .NET Blazor 方案/專案，改成都使用 SmartTask 的命名規則，所有之前有用到 MyProject 的地方都改成 SmartTask，例如 方案名稱、各種檔案名稱、命名空間等等，當變更檔案名稱之後，原有的專案參考規則也需要保留下來，確保專案之間的引用不會出現問題。

