using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Views.Admins;

namespace MyProject.Tests.Components;

/// <summary>
/// 「系統巡檢報告」頁（0.10.39 起）：只限管理員；報告內容視窗的全面摘要在最前面、AI 說明經安全管線、容量與目錄的顯示。
/// HTTP 換成記憶體裡的假 API。清單頁本身有 AntDesign 的 Select（bUnit 沒有瀏覽器量不到下拉框位置），以實機腳本
/// <c>scripts/e2e/system-reports.mjs</c> 驗證。
/// </summary>
public sealed class SystemReportViewTests : ComponentTestBase
{
    private static readonly Guid ReportId = Guid.NewGuid();
    private readonly FakeHandler handler = new();
    private readonly CurrentUserService currentUser = new();

    public SystemReportViewTests()
    {
        Services.AddLogging();
        Services.AddSingleton(currentUser);
        TestPagePermissions.AddTo(Services);
        Services.AddSingleton(sp =>
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
            var navigation = sp.GetRequiredService<NavigationManager>();
            var session = new ClientSession(http, new TokenStore(sp.GetRequiredService<IJSRuntime>()), navigation, NullLogger<ClientSession>.Instance);
            return new SystemReportApi(new ApiClient(http, session, navigation, NullLogger<ApiClient>.Instance));
        });
    }

    [Fact]
    public void NonAdmin_SeesNoData()
    {
        currentUser.CurrentUser.IsAuthenticated = true;

        var cut = Render<SystemReportView>();

        Assert.Contains("alert-danger", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DetailModal_SummaryFirst_NarrativeSanitized_CapacityShown()
    {
        var cut = Render<SystemReportDetailModal>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(ReportId));

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("系統巡檢日報　2026-11-19", markup, StringComparison.Ordinal);
            Assert.Contains("紅燈・已發生問題", markup, StringComparison.Ordinal);
            Assert.Contains("有 1 個已發生的問題需要馬上處理", markup, StringComparison.Ordinal);
            Assert.Contains("已發生問題（要馬上處理）", markup, StringComparison.Ordinal);
            Assert.Contains("即將發生（有期限）", markup, StringComparison.Ordinal);
            Assert.Contains("注意即可", markup, StringComparison.Ordinal);
            Assert.Contains("（期限 2026-12-01）", markup, StringComparison.Ordinal);
            Assert.Contains("需要準備擴充容量", markup, StringComparison.Ordinal);
            Assert.Contains("照目前的狀況", markup, StringComparison.Ordinal);
            Assert.Contains("<strong>整體</strong>", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", markup, StringComparison.Ordinal);
            Assert.Contains("資料累積中（3 天）", markup, StringComparison.Ordinal);
            Assert.Contains("至少", markup, StringComparison.Ordinal);
            Assert.Contains("沒有設定或不存在", markup, StringComparison.Ordinal);
            Assert.Contains("href=\"/backups\"", markup, StringComparison.Ordinal);
            Assert.Contains("日誌管線", markup, StringComparison.Ordinal);
            Assert.True(markup.IndexOf("已發生問題（要馬上處理）", StringComparison.Ordinal) < markup.IndexOf("AI 說明", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task DetailModal_Missing_ShowsTheServerMessage()
    {
        var cut = Render<SystemReportDetailModal>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(Guid.NewGuid()));

        cut.WaitForAssertion(() => Assert.Contains("找不到這份報告", cut.Markup, StringComparison.Ordinal));
    }

    private void SignInAsAdmin()
    {
        currentUser.CurrentUser.Id = 1;
        currentUser.CurrentUser.Account = "support";
        currentUser.CurrentUser.IsAuthenticated = true;
        currentUser.CurrentUser.IsAdmin = true;
    }

    private static SystemReportContentDto Content() => new()
    {
        ReportId = ReportId,
        Frequency = SystemReportFrequencies.Daily,
        PeriodStart = new DateTime(2026, 11, 19),
        PeriodEnd = new DateTime(2026, 11, 20),
        CreatedAt = new DateTime(2026, 11, 20, 7, 30, 0),
        SystemName = "測試系統",
        SystemVersion = "0.10.39",
        Level = SystemReportLevels.Red,
        Verdict = "有 1 個已發生的問題需要馬上處理，另有 1 個即將發生的狀況。",
        Findings =
        [
            new() { RuleId = "BKP-NONE", Section = SystemReportSections.Backup, Level = SystemReportLevels.Red, Title = "沒有任何系統備份", Detail = "備份目錄裡沒有任何備份檔。", IfIgnored = "救不回來。", HowToFix = "立即備份。", AfterFix = "有備份。", ActionRoute = "/backups" },
            new() { RuleId = "CAP-VOLUME", Section = SystemReportSections.Capacity, Level = SystemReportLevels.Orange, Title = "磁碟 D:\\ 空間不足", Detail = "剩 10%。", IfIgnored = "寫入失敗。", HowToFix = "擴充。", AfterFix = "足夠。", Deadline = new DateTime(2026, 12, 1) },
            new() { RuleId = "SEC-ADMIN2FA", Section = SystemReportSections.Security, Level = SystemReportLevels.Yellow, Title = "有管理員沒有啟用兩步驟驗證", Detail = "1 位。", IfIgnored = "風險。", HowToFix = "啟用。", AfterFix = "安全。" },
        ],
        Expansions = [new() { Volume = "D:\\（資料庫）", FullDate = new DateTime(2026, 12, 1), SuggestedBytes = 100L * 1024 * 1024 * 1024 }],
        Predictions = ["照目前的狀況，下一期應該維持正常。"],
        Volumes =
        [
            new() { Name = "C:\\", Purposes = "程式", TotalBytes = 100, FreeBytes = 50, FreePercent = 50, SampleDays = 3 },
            new() { Name = "D:\\", Purposes = "資料庫", TotalBytes = 100, FreeBytes = 10, FreePercent = 10, DailyGrowthBytes = 1, DaysToFull = 10, FullDate = new DateTime(2026, 12, 1), SampleDays = 30 },
        ],
        Folders =
        [
            new() { Name = "專案附件", Volume = "D:\\", SizeBytes = 2048, ItemCount = 3, Status = CapacitySnapshotKinds.StatusPartial },
            new() { Name = "上傳暫存", Status = CapacitySnapshotKinds.StatusMissing },
        ],
        Database = new() { MainBytes = 4096, WalBytes = 1024, FreePageBytes = 0 },
        Stats = [new() { Section = SystemReportSections.Errors, Label = "Error 以上的日誌", Value = "3 筆" }],
        Limitations = ["日誌管線與告警的計數從網站上次啟動起算，重新啟動後歸零。"],
        Narrative = "## 整體狀況\n**整體**還好。<script>alert(1)</script>",
        NarrativeStatus = SystemReportNarrativeStatuses.Written,
    };

    private static SystemReportSummaryDto Summary() => new()
    {
        ReportId = ReportId,
        Frequency = SystemReportFrequencies.Daily,
        PeriodStart = new DateTime(2026, 11, 19),
        PeriodEnd = new DateTime(2026, 11, 20),
        CreatedAt = new DateTime(2026, 11, 20, 7, 30, 0),
        Level = SystemReportLevels.Red,
        RedCount = 1,
        OrangeCount = 2,
        YellowCount = 3,
        Trigger = SystemReportTriggers.Schedule,
        NarrativeStatus = SystemReportNarrativeStatuses.Written,
        DeliveryStatus = SystemReportDeliveryStatuses.Sent,
    };

    /// <summary>假的系統巡檢報告 API。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((path, body));
            const string Base = "/api/v1/system-reports";
            return path switch
            {
                Base + "/search" => Json(HttpStatusCode.OK, ApiResult<SystemReportListDto>.SuccessResult(new SystemReportListDto
                {
                    Items = [Summary()],
                    TotalCount = 1,
                    PageIndex = 1,
                    PageSize = 20,
                    DailyRetentionDays = 90,
                })),
                Base + "/generate" => Json(HttpStatusCode.OK, ApiResult<object>.SuccessResult(new { }, "已開始產生報告。")),
                Base + "/legacy" => Json(HttpStatusCode.OK, ApiResult<AdminPatrolReportListDto>.SuccessResult(new AdminPatrolReportListDto { RetentionDays = 90 })),
                _ when path == $"{Base}/{ReportId}" => Json(HttpStatusCode.OK, ApiResult<SystemReportDetailDto>.SuccessResult(new SystemReportDetailDto { Summary = Summary(), Content = Content() })),
                _ => Json(HttpStatusCode.NotFound, ApiResult<object>.NotFoundResult("找不到這份報告，可能已超過保存期限被清除。")),
            };
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, ApiResult<T> body)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
