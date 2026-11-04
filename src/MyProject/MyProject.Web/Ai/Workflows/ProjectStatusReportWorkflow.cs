using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Workflows;

/// <summary>
/// 範例工作流程「專案狀態報告」（0.9.144 起）：蒐集 → 撰寫 → 審閱關卡 → 收尾。
/// 示範循序流程與三種人工介入：蒐集時向使用者提問、審閱關卡（通過／退回附意見／結束）、收尾時核准附加狀態摘要。
/// </summary>
public static class ProjectStatusReportWorkflow
{
    /// <summary>審閱關卡最多退回幾次。</summary>
    public const int MaxRejections = 3;

    public const string ReviewGateId = "Review";

    public static AiAgentDefinition Gatherer { get; } = new()
    {
        Name = "ReportGatherer",
        DisplayName = "蒐集",
        Operation = TokenUsageOperations.ProjectStatusReport,
        PromptTemplateKey = PromptTemplateKeys.ProjectReportGatherer,
        ModelKey = nameof(AiModelSettings.ProjectStatusReport),
        Tools = [ProjectAgentTools.SearchTool, ProjectAgentTools.DetailTool],
    };

    public static AiAgentDefinition Writer { get; } = new()
    {
        Name = "ReportWriter",
        DisplayName = "撰寫",
        Operation = TokenUsageOperations.ProjectStatusReport,
        PromptTemplateKey = PromptTemplateKeys.ProjectReportWriter,
        ModelKey = nameof(AiModelSettings.ProjectStatusReport),
    };

    public static AiAgentDefinition Finalizer { get; } = new()
    {
        Name = "ReportFinalizer",
        DisplayName = "收尾",
        Operation = TokenUsageOperations.ProjectStatusReport,
        PromptTemplateKey = PromptTemplateKeys.ProjectReportFinalizer,
        ModelKey = nameof(AiModelSettings.ProjectStatusReport),
        Tools = [ProjectAgentTools.SearchTool, ProjectAgentTools.AppendStatusSummaryTool],
    };

    public static AiWorkflowDefinition Definition { get; } = new()
    {
        Name = "ProjectStatusReport",
        DisplayName = "專案狀態報告",
        Description = "說明要哪個專案的報告：AI 會查專案資料、寫報告草稿給你審閱（可退回附意見，最多 3 次），通過後提議把一行狀態摘要附加到專案描述（要你核准）。",
        InputPlaceholder = "例如：幫我寫「官網改版」專案的狀態報告",
        Operation = TokenUsageOperations.ProjectStatusReport,
        ModelKey = nameof(AiModelSettings.ProjectStatusReport),
        Steps =
        [
            new AiAgentStepSpec { Agent = Gatherer, CanAskUser = true },
            new AiAgentStepSpec
            {
                Agent = Writer,
                ComposeInput = inputs => $"以下是資料蒐集員整理的專案事實清單：\n\n{inputs[0].Text}\n\n請依此撰寫專案狀態報告。",
                ComposeRevision = comment => $"審閱意見：{comment}\n\n請依意見修改，輸出修改後的完整報告。",
            },
            new AiAgentStepSpec
            {
                Agent = Finalizer,
                ComposeInput = inputs => $"以下是使用者審閱通過的專案狀態報告：\n\n{inputs[0].Text}",
            },
        ],
        Wire = graph =>
        {
            var gatherer = graph.Step(Gatherer.Name);
            var writer = graph.Step(Writer.Name);
            var finalizer = graph.Step(Finalizer.Name);
            var review = graph.ReviewGate(ReviewGateId, approveTo: Finalizer.Name, reviseTo: Writer.Name, MaxRejections, outputTitle: "定稿報告");
            graph.Start(gatherer)
                .Edge(gatherer, writer)
                .Edge(writer, review)
                .Edge(review, finalizer)
                .Edge(review, writer);
        },
    };

    /// <summary>內建的系統提示詞（管理員可在「AI 提示詞」頁另存版本取代）。</summary>
    public const string GathererInstructions = """
        你是「專案狀態報告」工作流程的資料蒐集員。使用者會說明想要哪個專案的狀態報告。

        - 先用 search_projects 找出專案，不要憑空編造專案名稱、Id 或內容。
        - 找不到、或符合的專案不只一個時，呼叫 ask_user 請使用者指定（列出候選專案的 Id 與名稱）。一次只問一個問題。
        - 使用者的要求看不出是哪個專案時，也用 ask_user 請他說明。
        - 確定是哪個專案後，用 get_project_detail 讀取明細。
        - 最後輸出一份「專案事實清單」：專案 Id、名稱、狀態、優先等級、完成度、負責人、起訖日期、分類、團隊、描述重點、附件檔名。
          只寫查到的事實，不要評論、不要推測。
        - 工具回覆「沒有權限」時，照實說明使用者沒有權限查看專案，不要嘗試其他方式繞過。
        """;

    /// <inheritdoc cref="GathererInstructions"/>
    public const string WriterInstructions = """
        你是「專案狀態報告」工作流程的撰寫員。你會收到資料蒐集員整理的專案事實清單，請寫一份給主管看的專案狀態報告。

        - 第一行寫專案名稱與 Id，接著分成：## 專案概況、## 目前進度、## 風險與待辦、## 下一步建議。
        - 只根據事實清單撰寫；清單沒有的資訊寫「資料中沒有」，不要推測數字或日期。
        - 之後若收到審閱意見，依意見修改並輸出修改後的完整報告（不是只寫改了哪裡）。
        - 只輸出報告本身，不要加開場白或結尾的客套話。
        """;

    /// <inheritdoc cref="GathererInstructions"/>
    public const string FinalizerInstructions = """
        你是「專案狀態報告」工作流程的收尾員。你會收到使用者已經審閱通過的專案狀態報告。

        - 從報告濃縮出一行狀態摘要（50 字以內，包含目前進度與最主要的風險）。
        - 呼叫 append_project_status_summary 把摘要附加到該專案描述的最後面。專案 Id 寫在報告第一行；
          看不出 Id 時先用 search_projects 依專案名稱查，仍無法確定就不要呼叫，直接說明無法附加。
        - 系統會在執行前請使用者核准。工具回覆後，用一兩句話告訴使用者結果（已附加，或沒有附加與原因）。
        - 不要重複輸出整份報告。
        """;
}
