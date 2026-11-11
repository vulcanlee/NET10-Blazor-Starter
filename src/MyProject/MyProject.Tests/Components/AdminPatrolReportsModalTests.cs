using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Views.Admins;

namespace MyProject.Tests.Components;

/// <summary>
/// 系統管理助理頁的「巡檢報告」視窗（0.9.161 起；0.10.7 起在 WASM 執行，資料走 <c>api/v1/admin-assistant/patrol-reports</c>）：
/// 清單、打開一份報告（AI 產出經安全管線）、深連結找不到報告。HTTP 換成記憶體裡的假報告。
/// </summary>
public sealed class AdminPatrolReportsModalTests : ComponentTestBase
{
    private readonly FakeReportHandler handler = new();

    public AdminPatrolReportsModalTests()
    {
        Services.AddLogging();
        Services.AddSingleton(sp =>
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
            var navigation = sp.GetRequiredService<NavigationManager>();
            var session = new ClientSession(http, new TokenStore(sp.GetRequiredService<IJSRuntime>()), navigation, NullLogger<ClientSession>.Instance);
            return new AdminAssistantApi(new ApiClient(http, session, navigation, NullLogger<ApiClient>.Instance));
        });
    }

    [Fact]
    public async Task Open_ShowsListAndLatestReport_RenderedSafely()
    {
        Save(new DateTime(2026, 11, 7, 7, 30, 0), AdminPatrolStatuses.Normal, "", "## 總結\n昨天正常。");
        var latest = Save(
            new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Abnormal, "排程與備份",
            "## 總結\n**備份失敗**<script>alert(1)</script>");
        var cut = Render<AdminPatrolReportsModal>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2026/11/08 07:30", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("2026/11/07 07:30", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("排程與備份", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("<strong>備份失敗</strong>", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("保存 45 天", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal(latest, cut.Instance.SelectedReportId);
    }

    [Fact]
    public async Task OpenSpecificReport_SelectsIt()
    {
        var older = Save(new DateTime(2026, 11, 7, 7, 30, 0), AdminPatrolStatuses.Attention, "帳號與安全", "## 總結\n較舊的報告。");
        Save(new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Normal, "", "## 總結\n最新的報告。");
        var cut = Render<AdminPatrolReportsModal>();

        var found = await cut.InvokeAsync(() => cut.Instance.OpenAsync(older));

        Assert.True(found);
        cut.WaitForAssertion(() => Assert.Contains("較舊的報告。", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(older, cut.Instance.SelectedReportId);
    }

    [Fact]
    public async Task OpenMissingReport_ReturnsFalse_AndStillShowsList()
    {
        Save(new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Normal, "", "## 總結\n最新的報告。");
        var cut = Render<AdminPatrolReportsModal>();

        var found = await cut.InvokeAsync(() => cut.Instance.OpenAsync(Guid.NewGuid()));

        Assert.False(found);
        cut.WaitForAssertion(() => Assert.Contains("最新的報告。", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoReports_ShowsEmptyHint()
    {
        var cut = Render<AdminPatrolReportsModal>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        cut.WaitForAssertion(() => Assert.Contains("還沒有巡檢報告", cut.Markup, StringComparison.Ordinal));
    }

    private Guid Save(DateTime createdAt, string status, string attention, string markdown)
    {
        var id = Guid.NewGuid();
        handler.Reports.Insert(0, new AdminPatrolReportDetailDto
        {
            Summary = new AdminPatrolReportSummaryDto
            {
                ReportId = id,
                CreatedAt = createdAt,
                Status = status,
                AttentionSections = attention,
                Trigger = JobRunTriggers.Schedule,
                ModelName = "gpt-test",
            },
            Markdown = markdown,
        });
        return id;
    }

    /// <summary>假的巡檢報告 API：清單（最新在前、保存 45 天）與單份，找不到 404。</summary>
    private sealed class FakeReportHandler : HttpMessageHandler
    {
        public List<AdminPatrolReportDetailDto> Reports { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            const string Base = "/api/v1/admin-assistant/patrol-reports";
            if (path == Base)
            {
                return Json(HttpStatusCode.OK, ApiResult<AdminPatrolReportListDto>.SuccessResult(new AdminPatrolReportListDto
                {
                    RetentionDays = 45,
                    Items = Reports.Select(report => report.Summary).ToList(),
                }));
            }

            var id = Guid.Parse(path[(Base.Length + 1)..]);
            return Reports.FirstOrDefault(report => report.Summary.ReportId == id) is { } found
                ? Json(HttpStatusCode.OK, ApiResult<AdminPatrolReportDetailDto>.SuccessResult(found))
                : Json(HttpStatusCode.NotFound, ApiResult<AdminPatrolReportDetailDto>.NotFoundResult("找不到這份巡檢報告，可能已超過保存期限被清除。"));
        }

        private static Task<HttpResponseMessage> Json<T>(HttpStatusCode status, ApiResult<T> body)
            => Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) });
    }
}
