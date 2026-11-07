using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// AI 對話紀錄 PDF（0.9.72 起）：中繼資料、角色標示與各種結果都排得出來。
/// 標 <c>[Collection("PdfFont")]</c> 的理由同 <see cref="AiReportPdfBuilderTests"/>（字型註冊是 process 全域）。
/// </summary>
[Collection("PdfFont")]
public sealed class AiCallLogPdfBuilderTests
{
    [Fact]
    public void Build_ShouldProducePdf_ForFullConversation()
    {
        var bytes = AiCallLogPdfBuilder.Build(CreateRequest(success: true, responseText: "## 結論\n\n一切正常。"));

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Build_ShouldProducePdf_WhenNothingWasReceived()
    {
        var bytes = AiCallLogPdfBuilder.Build(CreateRequest(success: false, responseText: string.Empty) with { Messages = [] });

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public void BuildMetadataLines_ShouldIncludeOperationAccountUsageAndCallId()
    {
        var request = CreateRequest(success: true, responseText: "ok");

        var lines = AiCallLogPdfBuilder.BuildMetadataLines(request).ToDictionary(x => x.Key, x => x.Value);

        Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, lines["作業"]);
        Assert.Equal("chen", lines["發起者"]);
        Assert.Equal("例外紀錄 #12（NullReferenceException）", lines["關聯說明"]);
        Assert.Contains("NT$ 1.50", lines["用量"], StringComparison.Ordinal);
        Assert.Equal(request.Item.CallId.ToString(), lines["呼叫識別碼"]);
    }

    [Fact]
    public void BuildMetadataLines_ShouldSayUnpriced_WhenUsageHasNoCost()
    {
        var request = CreateRequest(success: true, responseText: "ok") with
        {
            Usage = new TokenUsageLogAdapterModel { InputCount = 100, OutputCount = 50 },
        };

        var lines = AiCallLogPdfBuilder.BuildMetadataLines(request).ToDictionary(x => x.Key, x => x.Value);

        Assert.EndsWith("，未定價", lines["用量"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("system", "系統提示（System）")]
    [InlineData("user", "使用者（User）")]
    [InlineData("assistant", "AI 助理（Assistant）")]
    [InlineData("tool", "工具結果（Tool）")]
    [InlineData("developer", "developer")]
    [InlineData("", "（未標示角色）")]
    public void RoleLabel_ShouldNameEachRole(string role, string expected)
        => Assert.Equal(expected, AiCallLogPdfBuilder.RoleLabel(role));

    [Fact]
    public void DescribeOutcome_ShouldDistinguishCanceledFromFailed()
    {
        Assert.Equal("成功", AiCallLogPdfBuilder.DescribeOutcome(new AiCallLogAdapterModel { Success = true }));
        Assert.Equal("已取消", AiCallLogPdfBuilder.DescribeOutcome(new AiCallLogAdapterModel { FailureReason = "Canceled" }));
        Assert.Equal("失敗（Timeout）", AiCallLogPdfBuilder.DescribeOutcome(new AiCallLogAdapterModel { FailureReason = "Timeout" }));
    }

    // ---------- 合併匯出（0.9.169 起） ----------

    [Fact]
    public void BuildMerged_ShouldProducePdf_IncludingMissingContent()
    {
        var bytes = AiCallLogPdfBuilder.BuildMerged(CreateMerged(
            CreateRequest(success: true, responseText: "## 結論\n\n一切正常。"),
            CreateRequest(success: false, responseText: string.Empty) with { Messages = [], ContentMissing = true }));

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    /// <summary>開頭的索引表：依送出時間由舊到新，序號、時間、作業、發起者、結果。</summary>
    [Fact]
    public void BuildIndexRows_ShouldListEachRecordOldestFirst()
    {
        var newer = CreateRequest(success: true, responseText: "ok");
        var older = CreateRequest(success: false, responseText: string.Empty) with
        {
            Item = new AiCallLogAdapterModel
            {
                Id = 2,
                OccurredAt = newer.Item.OccurredAt.AddHours(-1),
                Operation = TokenUsageOperations.AiExceptionAnalysis,
                Account = null,
                Success = false,
                FailureReason = "Timeout",
            },
        };

        var rows = AiCallLogPdfBuilder.BuildIndexRows(CreateMerged(newer, older));

        Assert.Equal(
            [
                ["1", "2026-09-30 08:30:00", TokenUsageOperations.AiExceptionAnalysis, "（系統自動）", "失敗（Timeout）"],
                ["2", "2026-09-30 09:30:00", TokenUsageOperations.AiExceptionAnalysis, "chen", "成功"],
            ],
            rows.Select(x => x.ToArray()).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void BuildMerged_ShouldRejectEmptyOrTooMany(int count)
    {
        var items = Enumerable.Range(0, count).Select(_ => CreateRequest(success: true, responseText: "ok")).ToArray();

        Assert.Throws<ArgumentException>(() => AiCallLogPdfBuilder.BuildMerged(CreateMerged(items)));
        Assert.Equal(50, AiCallLogPdfBuilder.MaxMergedRecords);
    }

    /// <summary>合併匯出每段只收 5 萬字（單筆仍是 20 萬字），截斷時註明去哪裡看完整內容。</summary>
    [Fact]
    public void ClipContent_ShouldUseTheMergedLimit_AndSayWhereToFindTheRest()
    {
        var content = new string('字', AiCallLogPdfBuilder.MaxMergedBlockCharacters + 1);

        var (merged, mergedNote) = AiCallLogPdfBuilder.ClipContent(content, merged: true);
        var (single, singleNote) = AiCallLogPdfBuilder.ClipContent(content, merged: false);

        Assert.Equal(50_000, merged.Length);
        Assert.Contains("完整內容請開啟單筆紀錄或下載 JSON", mergedNote, StringComparison.Ordinal);
        Assert.Equal(content, single);
        Assert.Null(singleNote);
    }

    /// <summary>
    /// 審查發現（0.9.169）：Agent 對話每次呼叫都重送整段歷史，整段對話合併時內容會隨呼叫次數平方成長，
    /// 所以整份合併 PDF 另有總字數上限；用完之後的內容段只留一行註明。
    /// </summary>
    [Fact]
    public void MergedContentBudget_ShouldStopIncludingContent_WhenTheWholeDocumentIsFull()
    {
        var budget = new AiCallLogPdfBuilder.MergedContentBudget(120_000);
        var block = new string('字', 60_000);

        var first = budget.Take(block);
        var second = budget.Take(block);
        var third = budget.Take(block);
        var fourth = budget.Take("短短一段");

        Assert.Equal(AiCallLogPdfBuilder.MaxMergedBlockCharacters, first.Text.Length);
        Assert.Equal(AiCallLogPdfBuilder.MaxMergedBlockCharacters, second.Text.Length);
        Assert.Equal(20_000, third.Text.Length);
        Assert.Contains("整份 PDF", third.Note, StringComparison.Ordinal);
        Assert.Equal(string.Empty, fourth.Text);
        Assert.Contains("整份 PDF", fourth.Note, StringComparison.Ordinal);
        Assert.Equal(1_000_000, AiCallLogPdfBuilder.MaxMergedTotalCharacters);
    }

    private static AiCallLogMergedPdfRequest CreateMerged(params AiCallLogPdfRequest[] items) => new()
    {
        SystemName = "企業管理平台",
        SystemVersion = "0.9.169",
        OperatorAccount = "support",
        GeneratedAt = new DateTime(2026, 9, 30, 10, 0, 0),
        Items = items,
    };

    private static AiCallLogPdfRequest CreateRequest(bool success, string responseText) => new()
    {
        SystemName = "企業管理平台",
        SystemVersion = "0.9.72",
        OperatorAccount = "support",
        GeneratedAt = new DateTime(2026, 9, 30, 10, 0, 0),
        Item = new AiCallLogAdapterModel
        {
            Id = 1,
            CallId = Guid.NewGuid(),
            OccurredAt = new DateTime(2026, 9, 30, 9, 30, 0),
            Operation = TokenUsageOperations.AiExceptionAnalysis,
            Provider = "AzureOpenAI",
            Model = "gpt-a",
            Account = "chen",
            Success = success,
            FailureReason = success ? null : "Timeout",
            ElapsedMilliseconds = 4321,
            RelatedInfo = "例外紀錄 #12（NullReferenceException）",
        },
        Messages =
        [
            new AiCallLogMessage("system", "你是資深的 .NET 維運工程師。"),
            new AiCallLogMessage("user", "例外類型：NullReferenceException\n  at Foo.Bar()"),
        ],
        ResponseText = responseText,
        Usage = new TokenUsageLogAdapterModel { InputCount = 100, OutputCount = 50, CostTwd = 1.5, CostUsd = 0.05 },
    };
}
