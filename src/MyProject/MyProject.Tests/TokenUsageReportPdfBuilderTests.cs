using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// Token 用量 PDF 報表測試。
///
/// ⚠️ 必須標 <c>[Collection("PdfFont")]</c>，理由同 <see cref="AiReportPdfBuilderTests"/>：
/// <c>GlobalFontSettings.FontResolver</c> 是 process 全域 static 且 write-once，
/// 兩個 PDF 測試若落在不同的 test collection 會被 xUnit 平行執行而互撞。
///
/// 與 <see cref="AiReportPdfBuilderTests"/> 一樣刻意不解析 PDF 內容驗文字
/// （方案內沒有輕量的 PDF 文字抽取相依）；中文能不能印出來由
/// <c>AiReportPdfBuilderTests.Font_ShouldBeEmbeddedInWebAssembly</c> 這一支共同守住 ——
/// 兩個產生器用的是同一個 <see cref="EmbeddedFontResolver"/>。
/// </summary>
[Collection("PdfFont")]
public sealed class TokenUsageReportPdfBuilderTests
{
    [Fact]
    public void Build_ShouldProducePdfSignature()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest());

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    /// <summary>一筆資料都沒有時仍要印得出一份（只有標題與篩選條件）報表，不可丟例外。</summary>
    [Fact]
    public void Build_ShouldNotThrow_WhenThereIsNoData()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(new TokenUsageReportRequest
        {
            SystemName = "企業管理平台",
            SystemVersion = "0.9.14 (2026/09/16)",
            OperatorAccount = "support",
            GeneratedAt = new DateTime(2026, 9, 16, 15, 30, 0),
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>
    /// 明細有上限（200 筆），超過的部分不印。
    ///
    /// 上限存在的理由是 PDF 拿來看趨勢、逐筆對帳請用 CSV；這支測試守的是
    /// 「超量時不會爆掉，也不會真的把幾千列全排進去」。
    /// </summary>
    [Fact]
    public void Build_ShouldCapDetailRows()
    {
        var capped = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details = CreateDetails(200),
        });

        var overCap = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details = CreateDetails(600),
        });

        // 超過上限的部分沒有被排版，檔案大小不會再成長（差異只來自「已截斷」提示字串）。
        Assert.True(overCap.Length < capped.Length * 1.2);
    }

    /// <summary>依時長計費（沒有 token 數）的呼叫也要排得出來。</summary>
    [Fact]
    public void Build_ShouldNotThrow_ForDurationBilledRows()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details =
            [
                new TokenUsageLogAdapterModel
                {
                    Id = 1,
                    OccurredAt = new DateTime(2026, 9, 16, 12, 5, 0),
                    Operation = "語音轉檔",
                    CallKind = "Transcription",
                    Provider = "OpenAI",
                    Model = "whisper-2",
                    Account = "support",
                    DurationSeconds = 184,
                    ElapsedMilliseconds = 5200,
                    Success = true,
                },
            ],
        });

        Assert.True(bytes.Length > 1000);
    }

    /// <summary>失敗的呼叫帶著失敗原因，也要印得出來。</summary>
    [Fact]
    public void Build_ShouldNotThrow_ForFailedRows()
    {
        var bytes = TokenUsageReportPdfBuilder.Build(CreateRequest() with
        {
            Details =
            [
                new TokenUsageLogAdapterModel
                {
                    Id = 2,
                    OccurredAt = new DateTime(2026, 9, 16, 10, 15, 0),
                    Operation = "AI 日誌分析",
                    CallKind = "Chat",
                    Provider = "AzureOpenAI",
                    Model = "gpt-5.6-sol",
                    Account = null,
                    InputCount = 6400,
                    OutputCount = 0,
                    TotalCount = 6400,
                    ElapsedMilliseconds = 9100,
                    Success = false,
                    FailureReason = "EmptyResponse",
                },
            ],
        });

        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public void Build_ShouldThrow_WhenRequestIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => TokenUsageReportPdfBuilder.Build(null!));
    }

    [Fact]
    public void Build_ShouldSucceed_WhenEveryDetailIsUnpriced()
    {
        // 0.9.17 之前的紀錄沒有單價快照，整份報表的費用欄都會是 null ——
        // 那是常態，不是例外，不能讓匯出炸掉。
        var request = CreateRequest();
        request = request with
        {
            Summary = new TokenUsageSummary { CallCount = 12, UnpricedCount = 12 },
            Details = CreateDetails(12, priced: false),
        };

        var bytes = TokenUsageReportPdfBuilder.Build(request);

        Assert.NotEmpty(bytes);
    }

    private static TokenUsageReportRequest CreateRequest() => new()
    {
        SystemName = "企業管理平台",
        SystemVersion = "0.9.14 (2026/09/16)",
        OperatorAccount = "support",
        GeneratedAt = new DateTime(2026, 9, 16, 15, 30, 0),
        StartDate = new DateTime(2026, 8, 1),
        EndDate = new DateTime(2026, 9, 16),
        Account = "support",
        Summary = new TokenUsageSummary
        {
            InputCount = 32832,
            OutputCount = 6197,
            CachedInputCount = 9648,
            ReasoningCount = 1609,
            TotalCount = 39029,
            CostUsd = 1.234567,
            CostTwd = 38.888,
            UnpricedCount = 1,
            CallCount = 7,
        },
        ByAccount = [Group("support", 5), Group("vulcan", 2)],
        ByOperation = [Group("AI 日誌分析", 4), Group("文件摘要", 2), Group("語音轉檔", 1)],
        ByModel = [Group("gpt-5.6-sol", 3), Group("gpt-5.4", 3), Group("whisper-2", 1)],
        ByCallKind = [Group("Chat", 6), Group("Transcription", 1)],
        Details = CreateDetails(12),
    };

    private static TokenUsageGroupRow Group(string key, int callCount) => new()
    {
        Key = key,
        InputCount = 1000 * callCount,
        OutputCount = 200 * callCount,
        CachedInputCount = 300 * callCount,
        ReasoningCount = 50 * callCount,
        TotalCount = 1200 * callCount,
        CostUsd = 0.05 * callCount,
        CostTwd = 1.575 * callCount,
        CallCount = callCount,
    };

    private static List<TokenUsageLogAdapterModel> CreateDetails(int count, bool priced = true) =>
        [.. Enumerable.Range(1, count).Select(index => new TokenUsageLogAdapterModel
        {
            Id = index,
            OccurredAt = new DateTime(2026, 9, 16, 0, 0, 0).AddMinutes(index),
            Operation = "AI 日誌分析",
            CallKind = "Chat",
            Provider = "AzureOpenAI",
            Model = "gpt-5.6-sol",
            Account = "support",
            InputCount = 1000 + index,
            OutputCount = 200 + index,
            TotalCount = 1200 + (index * 2),
            CachedInputCount = 100,
            ReasoningCount = 20,
            ElapsedMilliseconds = 12000 + index,
            Success = true,
            CostUsd = priced ? 0.001 * index : null,
            CostTwd = priced ? 0.0315 * index : null,
            CostExchangeRate = priced ? 31.5 : null,
            CostPriceKey = priced ? "gpt-5.6-sol" : null,
        })];
}
