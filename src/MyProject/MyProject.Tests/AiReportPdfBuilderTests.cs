using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// PDF 報告測試。
///
/// ⚠️ 必須標 <c>[Collection]</c>：<c>GlobalFontSettings.FontResolver</c> 是 process 全域
/// static 且 write-once，xUnit 預設會平行執行不同的 test collection，兩個測試同時搶著
/// 註冊就會互撞。
///
/// 刻意不解析 PDF 內容驗文字（方案內沒有輕量的 PDF 文字抽取相依），中介資訊改以
/// <see cref="AiReportPdfBuilder.BuildMetadataLines"/> 這個純函式斷言。
/// </summary>
[Collection("PdfFont")]
public sealed class AiReportPdfBuilderTests
{
    /// <summary>
    /// 內嵌字型必須真的存在且是完整檔案。
    ///
    /// 這支測試是刻意存在的：字型缺失時 PDF <b>不會</b>報錯，只會整片變成空白方框，
    /// 在 CI 上完全靜默。長度門檻則是為了擋住「有人改用 Git LFS」——
    /// <c>actions/checkout</c> 預設不抓 LFS，會拿到幾百位元組的指標檔。
    /// </summary>
    [Fact]
    public void Font_ShouldBeEmbeddedInWebAssembly()
    {
        Assert.True(EmbeddedFontResolver.IsFontAvailable(), "內嵌中文字型資源不存在。");
        Assert.True(
            EmbeddedFontResolver.GetFontByteCount() > 1_000_000,
            $"內嵌字型只有 {EmbeddedFontResolver.GetFontByteCount()} 位元組，看起來不是完整的字型檔。");
    }

    [Fact]
    public void EnsureRegistered_ShouldBeIdempotent()
    {
        for (var index = 0; index < 10; index++)
        {
            EmbeddedFontResolver.EnsureRegistered();
        }

        Assert.NotNull(PdfSharp.Fonts.GlobalFontSettings.FontResolver);
    }

    [Fact]
    public void EnsureRegistered_ShouldBeThreadSafe()
    {
        Parallel.For(0, 64, _ => EmbeddedFontResolver.EnsureRegistered());

        Assert.NotNull(PdfSharp.Fonts.GlobalFontSettings.FontResolver);
    }

    [Fact]
    public void ResolveTypeface_ShouldAlwaysReturnAFace()
    {
        var resolver = EmbeddedFontResolver.Instance;

        // 家族名稱刻意不比對，任何請求都要拿到字面，避免某一段悄悄掉字。
        Assert.NotNull(resolver.ResolveTypeface(EmbeddedFontResolver.FamilyName, false, false));
        Assert.NotNull(resolver.ResolveTypeface("Arial", true, true));
    }

    [Fact]
    public void Build_ShouldProducePdfSignature()
    {
        var bytes = AiReportPdfBuilder.Build(CreateRequest("## 總結\n\n一切正常。"));

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    /// <summary>模型可能輸出的所有語法都要能排版，不可丟例外。</summary>
    [Fact]
    public void Build_ShouldNotThrow_ForAllSupportedConstructs()
    {
        const string markdown = """
            ## 總結
            這批日誌共有 **三個** 重點問題，其中 `TimeoutException` 最嚴重。

            ### 細節
            - 第一項：資料庫連線逾時
              - 巢狀項目
            - 第二項：快取未命中

            1. 先確認連線字串
            2. 再確認防火牆規則

            ```
            System.TimeoutException: The operation has timed out.
               at MyProject.Business.Services.TeamService.GetAsync()
            ```

            > 這是一段引言，模型偶爾會用。

            ---

            結尾段落，包含 HTML 實體 &amp; 與行內 `code`。
            """;

        var bytes = AiReportPdfBuilder.Build(CreateRequest(markdown));

        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public void Build_ShouldNotThrow_WhenMarkdownIsEmpty()
    {
        var bytes = AiReportPdfBuilder.Build(CreateRequest(string.Empty));

        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public void Build_ShouldNotThrow_WhenMarkdownIsChineseOnly()
    {
        var bytes = AiReportPdfBuilder.Build(CreateRequest("這批日誌沒有任何英數字元，全部都是繁體中文說明。"));

        Assert.True(bytes.Length > 1000);
    }

    [Fact]
    public void Build_ShouldGrowWithContent()
    {
        var shortPdf = AiReportPdfBuilder.Build(CreateRequest("## 總結\n\n短。"));
        var longPdf = AiReportPdfBuilder.Build(
            CreateRequest("## 總結\n\n" + string.Join("\n\n", Enumerable.Repeat("這是一段相當長的分析說明文字。", 400))));

        Assert.True(longPdf.Length > shortPdf.Length);
    }

    [Fact]
    public void BuildMetadataLines_ShouldIncludeUsageAndModel_WhenPresent()
    {
        var request = CreateRequest("內容") with
        {
            ModelName = "gpt-4o-2024-11-20",
            Usage = new AiTokenUsage
            {
                InputCount = 31204,
                OutputCount = 872,
                TotalCount = 32076,
                CachedInputCount = 512,
                ReasoningCount = 128,
            },
        };

        var lines = AiReportPdfBuilder.BuildMetadataLines(request);
        var usage = lines.Single(line => line.Key == "用量").Value;

        Assert.Equal("gpt-4o-2024-11-20", lines.Single(line => line.Key == "使用模型").Value);
        Assert.Contains("輸入 31,204", usage);
        Assert.Contains("輸出 872", usage);
        Assert.Contains("合計 32,076", usage);
        Assert.Contains("快取輸入 512", usage);
        Assert.Contains("推論 128", usage);
    }

    [Fact]
    public void BuildMetadataLines_ShouldOmitUsage_WhenNull()
    {
        var lines = AiReportPdfBuilder.BuildMetadataLines(CreateRequest("內容"));

        Assert.DoesNotContain(lines, line => line.Key == "用量");
    }

    /// <summary>只有部分用量欄位有值時，缺的欄位不該出現在報告上。</summary>
    [Fact]
    public void BuildMetadataLines_ShouldOnlyListReturnedUsageFields()
    {
        var request = CreateRequest("內容") with
        {
            Usage = new AiTokenUsage { InputCount = 100, OutputCount = 20 },
        };

        var usage = AiReportPdfBuilder.BuildMetadataLines(request).Single(line => line.Key == "用量").Value;

        Assert.Contains("輸入 100", usage);
        Assert.Contains("輸出 20", usage);
        Assert.DoesNotContain("合計", usage);
        Assert.DoesNotContain("快取輸入", usage);
        Assert.DoesNotContain("推論", usage);
    }

    [Fact]
    public void BuildMetadataLines_ShouldOmitKeyword_WhenBlank()
    {
        var lines = AiReportPdfBuilder.BuildMetadataLines(CreateRequest("內容"));

        Assert.DoesNotContain(lines, line => line.Key == "關鍵字");
        Assert.Equal("不限", lines.Single(line => line.Key == "最低等級").Value);
    }

    [Fact]
    public void BuildMetadataLines_ShouldReportTruncation()
    {
        var request = CreateRequest("內容") with
        {
            Prompt = new AiPromptBuildResult
            {
                UserMessage = "log",
                TotalEntryCount = 357,
                IncludedEntryCount = 100,
                DroppedByEntryLimit = true,
            },
        };

        var scope = AiReportPdfBuilder.BuildMetadataLines(request).Single(line => line.Key == "送出範圍").Value;

        Assert.Contains("送出 100 筆／查詢 357 筆", scope);
        Assert.Contains("已依筆數上限取最新資料", scope);
    }

    private static AiReportPdfRequest CreateRequest(string markdown) => new()
    {
        SystemName = "企業管理平台",
        SystemVersion = "0.9.4 (2026/09/11)",
        OperatorAccount = "support",
        GeneratedAt = new DateTime(2026, 9, 11, 14, 30, 0),
        QueryStartTime = new DateTime(2026, 9, 11, 13, 0, 0),
        QueryEndTime = new DateTime(2026, 9, 11, 14, 0, 0),
        Markdown = markdown,
        Prompt = new AiPromptBuildResult
        {
            UserMessage = "以下是 2 筆應用程式日誌（時間正序，最舊在前），每筆以 --- 分隔。\n---\n第一筆\n---\n第二筆\n",
            TotalEntryCount = 2,
            IncludedEntryCount = 2,
        },
    };
}
