using System.Text;
using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Workflows;

/// <summary>
/// 範例工作流程「多觀點審查」（0.9.144 起）：三個審查 agent 同時看同一份文件（並行），收齊後由彙整 agent 合併成一份意見。
/// 示範並行分支（fan-out）與收齊（彙整步驟的 <see cref="AiAgentStepSpec.ExpectedInputs"/>）。
/// </summary>
public static class MultiPerspectiveReviewWorkflow
{
    public const string ForkId = "Dispatch";

    public static AiAgentDefinition Requirements { get; } = Reviewer("ReviewRequirements", "需求完整性", PromptTemplateKeys.ReviewRequirements);

    public static AiAgentDefinition Technical { get; } = Reviewer("ReviewTechnical", "技術可行性與風險", PromptTemplateKeys.ReviewTechnical);

    public static AiAgentDefinition Usability { get; } = Reviewer("ReviewUsability", "使用者體驗", PromptTemplateKeys.ReviewUsability);

    public static AiAgentDefinition Aggregator { get; } = Reviewer("ReviewAggregator", "彙整", PromptTemplateKeys.ReviewAggregator);

    private static readonly AiAgentDefinition[] Reviewers = [Requirements, Technical, Usability];

    public static AiWorkflowDefinition Definition { get; } = new()
    {
        Name = "MultiPerspectiveReview",
        DisplayName = "多觀點審查",
        Description = "貼上一份需求、規格或設計：需求完整性、技術可行性與風險、使用者體驗三位 AI 審查者同時審查，再彙整成一份意見。",
        InputPlaceholder = "貼上要審查的文件內容",
        Operation = TokenUsageOperations.MultiPerspectiveReview,
        ModelKey = nameof(AiModelSettings.MultiPerspectiveReview),
        Steps =
        [
            .. Reviewers.Select(reviewer => new AiAgentStepSpec
            {
                Agent = reviewer,
                ComposeInput = inputs => $"請審查以下文件：\n\n{inputs[0].Text}",
            }),
            new AiAgentStepSpec
            {
                Agent = Aggregator,
                ExpectedInputs = Reviewers.Length,
                ComposeInput = ComposeReviews,
                OutputTitle = "彙整意見",
            },
        ],
        Wire = graph =>
        {
            var fork = graph.Fork(ForkId);
            var reviewers = Reviewers.Select(reviewer => graph.Step(reviewer.Name)).ToArray();
            var aggregator = graph.Step(Aggregator.Name);
            graph.Start(fork).FanOut(fork, reviewers);
            foreach (var reviewer in reviewers)
            {
                graph.Edge(reviewer, aggregator);
            }
        },
    };

    /// <summary>彙整 agent 收到的訊息：依審查者分段（順序固定，不受誰先完成影響）。</summary>
    private static string ComposeReviews(IReadOnlyList<AiWorkflowMessage> inputs)
    {
        var text = new StringBuilder("以下是三位審查者對同一份文件的意見：\n");
        foreach (var reviewer in Reviewers)
        {
            var review = inputs.FirstOrDefault(input => input.From == reviewer.DisplayName)?.Text ?? "（沒有收到意見）";
            text.Append($"\n### {reviewer.DisplayName}\n{review}\n");
        }

        return text.ToString();
    }

    private static AiAgentDefinition Reviewer(string name, string displayName, string promptTemplateKey) => new()
    {
        Name = name,
        DisplayName = displayName,
        Operation = TokenUsageOperations.MultiPerspectiveReview,
        PromptTemplateKey = promptTemplateKey,
        ModelKey = nameof(AiModelSettings.MultiPerspectiveReview),
    };

    /// <summary>內建的系統提示詞（管理員可在「AI 提示詞」頁另存版本取代）。</summary>
    public const string RequirementsInstructions = """
        你是需求分析師，從「需求完整性」的角度審查使用者提供的文件（需求、規格或設計）。

        - 找出遺漏的情境與例外狀況、模糊或互相矛盾的描述、沒有驗收標準的項目。
        - 用項目清單列出問題，每項附上建議的補充方式；最多 8 項，依重要性排序。
        - 只談需求完整性，技術實作與使用者體驗交給其他審查者。
        """;

    /// <inheritdoc cref="RequirementsInstructions"/>
    public const string TechnicalInstructions = """
        你是資深工程師，從「技術可行性與風險」的角度審查使用者提供的文件（需求、規格或設計）。

        - 找出實作困難或成本被低估之處、效能與資安風險、資料一致性與相依系統的問題。
        - 用項目清單列出問題，每項說明風險與建議的處理方式；最多 8 項，依嚴重程度排序。
        - 只談技術可行性與風險，需求完整性與使用者體驗交給其他審查者。
        """;

    /// <inheritdoc cref="RequirementsInstructions"/>
    public const string UsabilityInstructions = """
        你是使用者體驗設計師，從「使用者體驗」的角度審查使用者提供的文件（需求、規格或設計）。

        - 找出操作流程繁瑣或容易出錯之處、用語不清楚、缺少回饋或錯誤處理說明的地方。
        - 用項目清單列出問題，每項附上改善建議；最多 8 項，依影響程度排序。
        - 只談使用者體驗，需求完整性與技術實作交給其他審查者。
        """;

    /// <inheritdoc cref="RequirementsInstructions"/>
    public const string AggregatorInstructions = """
        你是審查彙整人，會收到三位審查者（需求完整性、技術可行性與風險、使用者體驗）對同一份文件的意見。

        - 合併重複的意見，標出彼此衝突之處。
        - 輸出分成：## 總評（三到五句）、## 必須處理（依優先順序）、## 建議改善、## 意見衝突（沒有就寫「無」）。
        - 不要新增審查者沒有提到的問題。
        """;
}
