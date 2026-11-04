using Microsoft.Extensions.Logging;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>一次產生或清除測試專案的結果。<see cref="Titles"/> 是這次產生的專案名稱（清除時為空）。</summary>
public sealed record ProjectSampleResult(bool Success, int Count, string Message, IReadOnlyList<string> Titles);

/// <summary>
/// Agent 測試台的測試專案（0.9.145 起）：一鍵產生固定 10 筆繁體中文專案給 AI Agent 查詢、寫報告、審查，用完一鍵永久刪除。
///
/// <para>
/// 一律經 <see cref="ProjectService"/>：團隊範圍、驗證與稽核（<c>Project.Create</c>／<c>Delete</c>／<c>Purge</c>）照常生效，
/// 這裡不碰 DbContext。測試專案以標題開頭 <see cref="TitlePrefix"/> 辨認，不指定團隊（看得到專案的人與 Agent 都查得到）。
/// 權限（專案項目的新增／刪除）由畫面檢查，同專案頁。
/// </para>
/// </summary>
public sealed class ProjectSampleDataService
{
    /// <summary>測試專案的標題開頭。</summary>
    public const string TitlePrefix = "【測試】";

    /// <summary>找測試專案時一次讀幾筆（測試專案只有 10 筆，重複產生會被擋；留足夠空間給手動加的同名專案）。</summary>
    private const int MaxLookup = 500;

    private readonly ProjectService projectService;
    private readonly ILogger<ProjectSampleDataService> logger;

    public ProjectSampleDataService(ProjectService projectService, ILogger<ProjectSampleDataService> logger)
    {
        this.projectService = projectService;
        this.logger = logger;
    }

    /// <summary>
    /// 固定的 10 筆測試專案。日期以 <paramref name="today"/> 推算，所以「已逾期」「尚未開始」永遠成立；
    /// 描述都寫進度與風險，讓狀態報告與審查有內容可寫。
    /// </summary>
    public static IReadOnlyList<ProjectAdapterModel> Build(DateTime today)
    {
        var day = today.Date;
        return
        [
            Sample("官網改版", "進行中", "高", 65, "王小明", day.AddDays(-60), day.AddDays(30),
                "首頁與產品頁改為響應式設計，設計稿已定案，前端切版完成七成。風險：行銷部的新版文案延遲兩週交付，可能影響上線日。"),
            Sample("ERP 系統導入", "進行中", "高", 40, "陳美玲", day.AddDays(-120), day.AddDays(90),
                "財務與庫存模組已上線，採購模組正在轉換舊資料。風險：舊系統料號重複約 3%，需要人工清理後才能轉入。"),
            Sample("行動 App 第二期", "未開始", "中", 0, "林志豪", day.AddDays(14), day.AddDays(150),
                "新增推播通知與會員點數功能，需求訪談已排定。風險：iOS 審核規範變更，推播權限的說明文字可能要重寫。"),
            Sample("客服機器人", "已完成", "中", 100, "張雅婷", day.AddDays(-200), day.AddDays(-20),
                "常見問題自動回覆率達 62%，已移交客服部維運。後續只需每季更新問答資料。"),
            Sample("資安稽核改善", "進行中", "高", 80, "黃建國", day.AddDays(-90), day.AddDays(-5),
                "弱點修補剩 4 項中風險，滲透測試報告待複測。已逾期：外部資安廠商的複測排程延誤。"),
            Sample("資料倉儲建置", "暫緩", "低", 25, "吳佩君", day.AddDays(-150), day.AddDays(60),
                "因年度預算重新分配而暫緩。銷售資料的 ETL 已完成，庫存與財務資料尚未開始。"),
            Sample("員工教育訓練平台", "等待", "中", 50, "劉家豪", day.AddDays(-45), day.AddDays(45),
                "平台測試環境已建好，等待人資部提供第一批課程清單與講師名單。"),
            Sample("電子發票串接", "進行中", "高", 90, "蔡淑芬", day.AddDays(-30), day.AddDays(7),
                "與加值服務中心的串接測試已通過，等待正式環境憑證核發。風險：憑證若晚於下週核發，上線日會延後。"),
            Sample("倉儲條碼系統", "未開始", "低", 0, "許文傑", day.AddDays(30), day.AddDays(120),
                "規劃以手持裝置掃描條碼處理進出貨，預算已核准，下個月開始選購設備。"),
            Sample("會員資料整合", "進行中", "中", 30, "鄭怡君", day.AddDays(-20), day.AddDays(100),
                "整合官網、門市與 App 三邊的會員資料，個資法遵審查進行中。風險：重複會員的比對規則尚未定案。"),
        ];
    }

    /// <summary>產生 10 筆測試專案。已經有測試專案（含已刪除的）時不新增，請先清除。</summary>
    public async Task<ProjectSampleResult> GenerateAsync(DateTime today)
    {
        var (active, deleted) = await FindAsync();
        var existing = active.Count + deleted.Count;
        if (existing > 0)
        {
            logger.LogInformation("Sample project generation skipped because samples exist. Existing={Existing}", existing);
            return new ProjectSampleResult(false, 0, $"已經有 {existing} 筆測試專案（含已刪除的），請先按「清除測試專案」。", []);
        }

        var created = new List<string>();
        foreach (var sample in Build(today))
        {
            var result = await projectService.AddAsync(sample);
            if (result.Success == false)
            {
                logger.LogWarning("Sample project generation stopped. Created={Created}, Title={Title}", created.Count, sample.Title);
                return new ProjectSampleResult(false, created.Count, $"已建立 {created.Count} 筆，「{sample.Title}」建立失敗：{result.Message}", created);
            }

            created.Add(sample.Title);
        }

        logger.LogInformation("Sample projects generated. Count={Count}", created.Count);
        return new ProjectSampleResult(true, created.Count, $"已建立 {created.Count} 筆測試專案。", created);
    }

    /// <summary>永久刪除所有測試專案（含已刪除的）：啟用中的先刪除再永久刪除。不是測試專案的一律不動。</summary>
    public async Task<ProjectSampleResult> ClearAsync()
    {
        var (active, deleted) = await FindAsync();
        var removed = 0;
        foreach (var id in active)
        {
            var deleteResult = await projectService.DeleteAsync(id);
            if (deleteResult.Success == false)
            {
                return Failed(removed, deleteResult.Message);
            }

            deleted.Add(id);
        }

        foreach (var id in deleted)
        {
            var purgeResult = await projectService.PurgeAsync(id);
            if (purgeResult.Success == false)
            {
                return Failed(removed, purgeResult.Message);
            }

            removed++;
        }

        logger.LogInformation("Sample projects cleared. Count={Count}", removed);
        return new ProjectSampleResult(true, removed, removed == 0 ? "沒有測試專案。" : $"已永久刪除 {removed} 筆測試專案。", []);

        ProjectSampleResult Failed(int count, string message)
        {
            logger.LogWarning("Sample project clearing stopped. Removed={Removed}", count);
            return new ProjectSampleResult(false, count, $"已刪除 {count} 筆，其餘刪除失敗：{message}", []);
        }
    }

    /// <summary>找出標題以 <see cref="TitlePrefix"/> 開頭的專案（ProjectService 的搜尋是「包含」，這裡再篩一次）。</summary>
    private async Task<(List<int> Active, List<int> Deleted)> FindAsync()
    {
        var request = new DataRequest { Search = TitlePrefix, CurrentPage = 1, PageSize = MaxLookup, Take = MaxLookup };
        var active = await projectService.GetAsync(request);
        var deleted = await projectService.GetDeletedAsync(request);
        return (Ids(active.Result), Ids(deleted.Result));

        static List<int> Ids(IEnumerable<ProjectAdapterModel> items)
            => items.Where(item => item.Title.StartsWith(TitlePrefix, StringComparison.Ordinal)).Select(item => item.Id).ToList();
    }

    private static ProjectAdapterModel Sample(
        string name, string status, string priority, int completion, string owner, DateTime start, DateTime end, string description)
        => new()
        {
            Title = TitlePrefix + name,
            Status = status,
            Priority = priority,
            CompletionPercentage = completion,
            Owner = owner,
            StartDate = start,
            EndDate = end,
            Description = description,
        };
}
