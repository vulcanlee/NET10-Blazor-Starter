using System.Text;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.Admins
{
    public partial class ExceptionLogView
    {
        /// <summary>
        /// 「清除很久沒再發生的紀錄」門檻。刻意是程式常數而非設定鍵 ——
        /// 這是管理員按一下才會發生的動作，不需要每個部署各自調整。
        /// </summary>
        private const int PurgeDays = 90;

        private readonly ILogger<ExceptionLogView> logger;
        private readonly ExceptionLogService exceptionLogService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private ITable? table;

        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedSource = string.Empty;
        private string accountFilter = string.Empty;
        private string keyword = string.Empty;

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private string RoleMessage = string.Empty;

        private List<ExceptionLogAdapterModel> exceptionLogAdapterModels = [];

        // 明細窗：清單放不下的欄位與完整堆疊都在這裡看。
        // 一次只會開一筆，所以不需要快取字典 —— 開窗當下才讀檔。
        private bool detailVisible;
        private ExceptionLogAdapterModel? detailItem;
        private string detailStackTrace = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        public ExceptionLogView(
            ILogger<ExceptionLogView> logger,
            ExceptionLogService exceptionLogService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.exceptionLogService = exceptionLogService;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何例外內容。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Exception log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();

            try
            {
                var query = new ExceptionLogQuery
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    Source = selectedSource,
                    Account = accountFilter,
                    Keyword = keyword,
                    CurrentPage = _pageIndex,
                    PageSize = _pageSize,
                    SortField = sortField,
                    SortDescending = sortDirection switch
                    {
                        "descend" => true,
                        "ascend" => false,
                        _ => null,
                    },
                };

                var result = await exceptionLogService.GetAsync(query);
                exceptionLogAdapterModels = result.Result.ToList();
                _total = result.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load exception logs.");
                ViewNotification.Error(notificationService, $"載入例外紀錄失敗：{ex.GetType().Name}。");
                exceptionLogAdapterModels = [];
                _total = 0;
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private async Task OnTableChange(QueryModel<ExceptionLogAdapterModel> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;

            if (args.SortModel?.Any() == true)
            {
                var tableSortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
                sortDirection = tableSortModel.SortDirection.ToString() ?? string.Empty;
                sortField = TableSortHelper.ResolveSortFieldName(tableSortModel);
            }
            else
            {
                sortField = string.Empty;
                sortDirection = "None";
            }

            await ReloadAsync();
        }

        private async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Exception log search triggered.");
            await ReloadAsync();
        }

        private async Task OnRefreshAsync()
        {
            logger.LogInformation("Exception log refresh triggered.");
            await ReloadAsync();
        }

        /// <summary>
        /// 開啟明細窗。堆疊檔在開窗當下才讀 —— 清單查詢不應該一次把所有檔案讀進記憶體。
        /// </summary>
        private async Task OnViewAsync(ExceptionLogAdapterModel item)
        {
            detailItem = item;
            detailStackTrace = "讀取中…";
            detailVisible = true;
            StateHasChanged();

            var stackTrace = await exceptionLogService.GetStackTraceAsync(item.Id);
            detailStackTrace = string.IsNullOrWhiteSpace(stackTrace)
                ? "堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。完整日誌請改由「日誌檢視」查詢。"
                : stackTrace;

            StateHasChanged();
        }

        /// <summary>明細窗的「使用者」顯示文字；只有帳號與 UserId，不含姓名／Email。</summary>
        private string DetailAccountText
        {
            get
            {
                if (detailItem is null)
                {
                    return "—";
                }

                var account = string.IsNullOrWhiteSpace(detailItem.Account) ? "—" : detailItem.Account;
                return detailItem.UserId is null ? account : $"{account}（UserId={detailItem.UserId}）";
            }
        }

        private async Task OnDeleteAsync(ExceptionLogAdapterModel item)
        {
            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "刪除例外紀錄",
                Content = "確定要刪除這一列嗎？相同的例外若再次發生，會重新出現並從 1 次開始計算。",
                OkText = "刪除",
                CancelText = "取消",
            });

            if (confirmed == false)
            {
                return;
            }

            var result = await exceptionLogService.DeleteAsync(item.Id);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "刪除成功");
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        private async Task OnPurgeAsync()
        {
            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = $"清除 {PurgeDays} 天未再發生的紀錄",
                Content = $"將刪除「最後發生」早於 {PurgeDays} 天前的所有紀錄與其堆疊檔案。此動作無法復原。",
                OkText = "清除",
                CancelText = "取消",
            });

            if (confirmed == false)
            {
                return;
            }

            var result = await exceptionLogService.PurgeAsync(PurgeDays);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        private async Task OnClearAllAsync()
        {
            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "清空全部例外紀錄",
                Content = "將刪除所有紀錄與整個堆疊檔案目錄。此動作無法復原。",
                OkText = "清空",
                CancelText = "取消",
            });

            if (confirmed == false)
            {
                return;
            }

            var result = await exceptionLogService.ClearAllAsync();
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "已清空全部例外紀錄");
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        private async Task OnExportAsync()
        {
            try
            {
                // 匯出目前查詢條件下的全部資料，而非只有當頁 —— 管理員要的是整份對照。
                var query = new ExceptionLogQuery
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    Source = selectedSource,
                    Account = accountFilter,
                    Keyword = keyword,
                    CurrentPage = 1,
                    PageSize = ExceptionLogService.MaxRows,
                    SortField = sortField,
                    SortDescending = sortDirection == "ascend" ? false : true,
                };

                var result = await exceptionLogService.GetAsync(query);

                var builder = new StringBuilder();
                builder.AppendLine("最後發生,次數,例外類型,訊息,來源,頁面,操作,記錄器,使用者,首次發生");
                foreach (var item in result.Result)
                {
                    builder.AppendLine(string.Join(',',
                        Csv(item.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
                        Csv(item.OccurrenceCount.ToString()),
                        Csv(item.ExceptionType),
                        Csv(item.Message),
                        Csv(item.Source),
                        Csv(item.Page),
                        Csv(item.Operation),
                        Csv(item.LoggerName),
                        Csv(item.Account),
                        Csv(item.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss"))));
                }

                // 加 BOM，否則 Excel 開啟繁體中文會亂碼。
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(builder.ToString());

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-exceptions-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, "text/csv");

                logger.LogInformation("Exception log export downloaded. Rows={Rows}", result.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Exception log export failed.");
                ViewNotification.Error(notificationService, $"匯出失敗：{ex.GetType().Name}。");
            }
        }

        /// <summary>CSV 欄位跳脫：雙引號加倍，整欄以雙引號包住，換行才不會把一列拆成兩列。</summary>
        private static string Csv(string? value)
            => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

        /// <summary>次數越多顏色越重，讓「重複數百次」的噪音一眼可辨。</summary>
        private static string GetCountColor(long count) => count switch
        {
            >= 100 => "red",
            >= 10 => "orange",
            _ => "default",
        };

        private static string GetSourceColor(string source) => source switch
        {
            ExceptionSources.Ui => "blue",
            ExceptionSources.WebApi => "purple",
            ExceptionSources.Startup => "gold",
            ExceptionSources.Background => "cyan",
            _ => "default",
        };
    }
}
