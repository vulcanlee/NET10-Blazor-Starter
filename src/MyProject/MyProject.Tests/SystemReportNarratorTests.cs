using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;
using MyProject.Web.SystemReports;

namespace MyProject.Tests;

/// <summary>
/// 系統巡檢報告的 AI 說明與通知（0.10.39 起）：什麼時候不呼叫 AI、失敗或格式不對時退回、呼叫時帶的作業名稱與對話編號；
/// 通知給全體管理員（同時寄信、以報告去重）並寫回通知狀態。
/// </summary>
public sealed class SystemReportNarratorTests : IAsyncLifetime
{
    private readonly FakeChatClient chat = new();
    private SqliteConnection connection = null!;
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Skipped_WhenDisabled_NotConfigured_OrDailyAllGreen()
    {
        var disabled = await Narrator(new SystemReportSettings { AiNarrativeEnabled = false }).NarrateAsync(Content(SystemReportLevels.Red), CancellationToken.None);
        var notConfigured = await Narrator(configured: false).NarrateAsync(Content(SystemReportLevels.Red), CancellationToken.None);
        var green = await Narrator().NarrateAsync(Content(SystemReportLevels.Green), CancellationToken.None);

        Assert.All([disabled, notConfigured, green], x => Assert.Equal(SystemReportNarrativeStatuses.Skipped, x.Status));
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Written_WithTheReportAsTheConversation()
    {
        chat.Result = new AiAnalysisResult { Success = true, Markdown = "## 整體狀況\n系統正常。\n## 建議處理順序\n1. 無\n## 下期預測\n維持。", ModelName = "gpt-test" };
        var content = Content(SystemReportLevels.Red);

        var narration = await Narrator().NarrateAsync(content, CancellationToken.None);

        Assert.Equal((SystemReportNarrativeStatuses.Written, "gpt-test"), (narration.Status, narration.ModelName));
        var request = Assert.Single(chat.Requests);
        Assert.Equal(TokenUsageOperations.SystemReportNarrative, request.Operation);
        Assert.Equal(content.ReportId, request.ConversationId);
        Assert.Equal(SystemReportNarrator.Timeout, request.Timeout);
        Assert.Contains("系統巡檢報告 日報 2026/11/19～2026/11/19", request.RelatedInfo, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fallback_WhenTheCallFails_OrTheFormatIsWrong()
    {
        chat.Result = AiAnalysisResult.Failure(AiAnalysisFailureReason.Timeout, "逾時");
        var failed = await Narrator().NarrateAsync(Content(SystemReportLevels.Orange), CancellationToken.None);
        chat.Result = new AiAnalysisResult { Success = true, Markdown = "系統很好。", ModelName = "gpt-test" };
        var wrongFormat = await Narrator().NarrateAsync(Content(SystemReportLevels.Orange), CancellationToken.None);

        Assert.Equal((SystemReportNarrativeStatuses.Fallback, null), (failed.Status, failed.Markdown));
        Assert.Equal((SystemReportNarrativeStatuses.Fallback, null), (wrongFormat.Status, wrongFormat.Markdown));
        Assert.NotNull(wrongFormat.Note);
    }

    [Fact]
    public async Task Delivery_TellsAllAdminsWithEmail_OncePerReport_AndRecordsTheStatus()
    {
        var reports = ReportService();
        var content = Content(SystemReportLevels.Red);
        await reports.SaveAsync(Draft(content));
        var sender = new RecordingNotificationSender();

        var sent = await Delivery(sender, reports).SendToAdminsAsync(content, CancellationToken.None);

        Assert.True(sent);
        var request = Assert.Single(sender.Requests);
        var attachment = Assert.Single(request.Email!.Attachments!);
        Assert.Equal((EmailKinds.SystemReport, "system-report-daily-20261119.pdf", "application/pdf"), (request.Email.Kind, attachment.FileName, attachment.ContentType));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(attachment.Content, 0, 5));
        Assert.Equal("完整報告請見附件 PDF（system-report-daily-20261119.pdf）。", request.Email.ExtraText);
        Assert.DoesNotContain("附件", request.Body, StringComparison.Ordinal);
        Assert.Equal((NotificationCategories.SystemReport, true, $"SystemReport:{content.ReportId:N}"), (request.Category, request.AlsoEmail, request.SourceKey));
        Assert.True(request.Target.Admins);
        Assert.Equal(SystemReportDelivery.Link(content.ReportId), request.Link);
        Assert.Equal(SystemReportDeliveryStatuses.Sent, (await reports.GetAsync(content.ReportId))!.Summary.DeliveryStatus);
    }

    [Fact]
    public async Task Delivery_Failure_IsRecorded_AndRequesterNoticeHasNoEmail()
    {
        var reports = ReportService();
        var content = Content(SystemReportLevels.Red);
        await reports.SaveAsync(Draft(content));
        var failing = new FailingNotificationSender();
        var recording = new RecordingNotificationSender();

        Assert.False(await Delivery(failing, reports).SendToAdminsAsync(content, CancellationToken.None));
        await Delivery(recording, reports).NotifyRequesterAsync(content, 7, CancellationToken.None);

        Assert.Equal(SystemReportDeliveryStatuses.Failed, (await reports.GetAsync(content.ReportId))!.Summary.DeliveryStatus);
        var notice = Assert.Single(recording.Requests);
        Assert.False(notice.AlsoEmail);
        Assert.Equal([7], notice.Target.UserIds);
        Assert.StartsWith("報告已產生：", notice.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_PdfTooLargeOrFailing_IsNotAttached_AndTheEmailSaysWhy()
    {
        var reports = ReportService();
        var content = Content(SystemReportLevels.Red);

        var tooLarge = new SystemReportDelivery(new RecordingNotificationSender(), reports, NullLogger<SystemReportDelivery>.Instance,
            _ => new byte[EmailAttachment.MaxTotalBytes + 1]).EmailOptions(content);
        var failing = new SystemReportDelivery(new RecordingNotificationSender(), reports, NullLogger<SystemReportDelivery>.Instance,
            _ => throw new InvalidOperationException("font")).EmailOptions(content);

        Assert.Null(tooLarge.Attachments);
        Assert.Contains("PDF 太大", tooLarge.ExtraText, StringComparison.Ordinal);
        Assert.Null(failing.Attachments);
        Assert.Contains("PDF 產生失敗", failing.ExtraText, StringComparison.Ordinal);
        Assert.All([tooLarge, failing], x => Assert.Equal(EmailKinds.SystemReport, x.Kind));
    }

    [Fact]
    public void Body_IsTruncated_AndListsAtMostFivePerLevel()
    {
        var content = Content(SystemReportLevels.Red);
        for (var i = 0; i < 40; i++)
        {
            content.Findings.Add(new SystemReportFindingDto { Level = SystemReportLevels.Red, Title = $"問題 {i} " + new string('長', 30) });
        }

        content.Expansions.Add(new SystemReportExpansionDto { Volume = "D:\\", FullDate = new DateTime(2027, 1, 1) });
        var body = SystemReportDelivery.Body(content);

        Assert.True(body.Length <= SystemReportDelivery.MaxBodyLength);
        Assert.Contains("- 等 41 項", body, StringComparison.Ordinal);
        Assert.DoesNotContain("問題 5 ", body, StringComparison.Ordinal);
    }

    private SystemReportNarrator Narrator(SystemReportSettings? settings = null, bool configured = true)
        => new(chat, new FakePromptProvider(),
            new StaticOptionsMonitor<AiSettings>(configured
                ? new AiSettings { Provider = "OpenAI", ApiKey = "test-key", Model = "gpt-4o" }
                : new AiSettings()),
            new StaticOptionsMonitor<SystemReportSettings>(settings ?? new SystemReportSettings()),
            NullLogger<SystemReportNarrator>.Instance);

    private static SystemReportDelivery Delivery(INotificationSender sender, SystemReportService reports)
        => new(sender, reports, NullLogger<SystemReportDelivery>.Instance);

    private SystemReportService ReportService()
    {
        var system = new SystemSettings();
        system.ExternalFileSystem.AiAgentConversationPath = rootPath;
        return new SystemReportService(
            new TestDbContextFactory(connection),
            new AdminPatrolReportFileStore(Options.Create(system), NullLogger<AdminPatrolReportFileStore>.Instance),
            new StaticOptionsMonitor<SystemReportSettings>(new SystemReportSettings()),
            NullLogger<SystemReportService>.Instance);
    }

    private static SystemReportContentDto Content(string level) => new()
    {
        ReportId = Guid.NewGuid(),
        Frequency = SystemReportFrequencies.Daily,
        PeriodStart = new DateTime(2026, 11, 19),
        PeriodEnd = new DateTime(2026, 11, 20),
        CreatedAt = new DateTime(2026, 11, 20, 7, 30, 0),
        Level = level,
        Verdict = "結論。",
        Findings = level == SystemReportLevels.Green ? [] : [new() { RuleId = "X", Level = level, Title = "某個問題", Detail = "現況。" }],
    };

    private static SystemReportDraft Draft(SystemReportContentDto content)
        => new(content.ReportId, content.Frequency, content.PeriodStart, content.PeriodEnd, content.CreatedAt, content.Level, 1, 0, 0,
            SystemReportTriggers.Schedule, null, 1, SystemReportNarrativeStatuses.Skipped, null, SystemReportDeliveryStatuses.NotRequired, "{}");

    private sealed class FakeChatClient : IAiChatCompletionClient
    {
        public List<AiChatCompletionRequest> Requests { get; } = [];

        public AiAnalysisResult Result { get; set; } = new() { Success = true, Markdown = "## 整體狀況\n好。" };

        public Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }

        public IAsyncEnumerable<AiStreamUpdate> StreamAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakePromptProvider : IAiSystemPromptProvider
    {
        public Task<string> GetAsync(string templateKey, CancellationToken cancellationToken = default)
            => Task.FromResult(AiPromptGuardrails.Compose(templateKey, AiPromptGuardrails.DefaultInstructions(templateKey)));

        public Task<string> GetInstructionsAsync(string templateKey, CancellationToken cancellationToken = default)
            => Task.FromResult(AiPromptGuardrails.DefaultInstructions(templateKey));
    }

    private sealed class FailingNotificationSender : INotificationSender
    {
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationSendResult(0, 0, Failed: true));
    }
}
