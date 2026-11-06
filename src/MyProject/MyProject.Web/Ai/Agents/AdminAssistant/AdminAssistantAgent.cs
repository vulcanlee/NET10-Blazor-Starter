using System.Text;
using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 「系統管理助理」（0.9.155 起）：只限管理員的單一對話入口，每則訊息先分派領域，再組出這一輪的 agent。
///
/// <para>
/// 每一輪的定義都叫 <see cref="Name"/>（對話存檔與稽核的 agent 名稱）；差別只在工具與提示詞：
/// <b>唯讀工具每一輪都全部載入</b>（避免分派漏選領域就查不到資料），異動工具只載入被選中領域的；
/// 提示詞是共同說明＋被選中領域的說明（沒有分出領域或巡檢時接上全部四個領域）。
/// </para>
/// 設計見 <c>docs/prd/系統管理助理-prd.md</c>。
/// </summary>
public static class AdminAssistantAgent
{
    public const string Name = "AdminAssistant";
    public const string DisplayName = "系統管理助理";

    /// <summary>四個專家領域（不含巡檢），依提示詞段落順序。</summary>
    public static IReadOnlyList<AdminAssistantDomain> ExpertDomains { get; } =
        [AdminAssistantDomain.Accounts, AdminAssistantDomain.Monitoring, AdminAssistantDomain.Ai, AdminAssistantDomain.Ops];

    /// <summary>每個專家領域的提示詞種類。</summary>
    public static IReadOnlyDictionary<AdminAssistantDomain, string> DomainPromptKeys { get; } = new Dictionary<AdminAssistantDomain, string>
    {
        [AdminAssistantDomain.Accounts] = PromptTemplateKeys.AdminAssistantAccounts,
        [AdminAssistantDomain.Monitoring] = PromptTemplateKeys.AdminAssistantMonitoring,
        [AdminAssistantDomain.Ai] = PromptTemplateKeys.AdminAssistantAi,
        [AdminAssistantDomain.Ops] = PromptTemplateKeys.AdminAssistantOps,
    };

    /// <summary>四個領域的全部唯讀工具（每一輪都載入）。</summary>
    public static IReadOnlyList<AiToolDefinition> ReadTools { get; } =
        [.. MonitoringAssistantTools.ReadTools, .. OpsAssistantTools.ReadTools];

    /// <summary>各領域的異動工具（只在該領域被分派到時載入）。</summary>
    public static IReadOnlyDictionary<AdminAssistantDomain, IReadOnlyList<AiToolDefinition>> MutatingTools { get; } =
        ExpertDomains.ToDictionary(domain => domain, _ => (IReadOnlyList<AiToolDefinition>)[]);

    /// <summary>開對話與接續用：全部工具（接續時才找得到待核准的工具）。</summary>
    public static AiAgentDefinition Home { get; } = Build([.. ReadTools, .. MutatingTools.Values.SelectMany(tools => tools)]);

    /// <summary>這一輪的定義：全部唯讀工具＋被選中領域的異動工具；巡檢或沒有分出領域時只有唯讀工具。</summary>
    public static AiAgentDefinition DefinitionFor(IReadOnlySet<AdminAssistantDomain> domains)
    {
        if (domains.Count == 0 || domains.Contains(AdminAssistantDomain.Patrol))
        {
            return Build(ReadTools);
        }

        return Build([.. ReadTools, .. ExpertDomains.Where(domains.Contains).SelectMany(domain => MutatingTools[domain])]);
    }

    /// <summary>
    /// 組這一輪的提示詞（不含固定規則，呼叫端再 <see cref="AiPromptGuardrails.Compose"/>）：
    /// 共同說明，接上每個被選中領域的「## 專家：名稱」段落；沒有分出領域或巡檢時接上全部四個領域。
    /// </summary>
    public static string ComposeInstructions(
        string baseText,
        IReadOnlyDictionary<AdminAssistantDomain, string> domainTexts,
        IReadOnlySet<AdminAssistantDomain> domains)
    {
        var all = domains.Count == 0 || domains.Contains(AdminAssistantDomain.Patrol);
        var builder = new StringBuilder(baseText.ReplaceLineEndings("\n").TrimEnd());
        foreach (var domain in ExpertDomains.Where(domain => all || domains.Contains(domain)))
        {
            builder.Append("\n\n## 專家：").Append(AdminAssistantRouting.NameOf(domain)).Append('\n')
                .Append(domainTexts[domain].ReplaceLineEndings("\n").Trim());
        }

        if (domains.Contains(AdminAssistantDomain.Patrol))
        {
            builder.Append("\n\n這一則是巡檢：請依共同說明的巡檢報告格式，用工具逐項查詢後產出報告。");
        }

        return builder.ToString();
    }

    private static AiAgentDefinition Build(IReadOnlyList<AiToolDefinition> tools) => new()
    {
        Name = Name,
        DisplayName = DisplayName,
        Operation = TokenUsageOperations.AdminAssistant,
        PromptTemplateKey = PromptTemplateKeys.AdminAssistant,
        ModelKey = nameof(AiModelSettings.AdminAssistant),
        Tools = tools,
    };

    /// <summary>共同說明（管理員可在「AI 提示詞」頁另存版本取代）。</summary>
    public const string DefaultCommonInstructions = """
        你是這套系統的「系統管理助理」，協助管理員查詢、診斷與維運系統。使用者一定是管理員。

        - 只根據工具查到的資料回答，不要憑空編造數字、帳號、時間或原因；查不到就照實說查不到。
        - 時間一律是伺服器本地時間，回答時寫出完整日期與時間。
        - 需要時可以連續呼叫多個工具交叉比對（例如排程失敗時，用執行紀錄的 TraceId 去查例外與日誌）。
        - 使用者要求的操作沒有對應的工具時，照實說明目前還不能代為操作，並指出要到系統管理底下的哪一頁自己操作。
        - 會異動資料的工具，系統會在執行前請使用者核准；被拒絕就照實告訴使用者沒有執行。異動前先確認對象正確。
        - 工具回覆「沒有權限」或「執行失敗」時照實轉告，不要嘗試繞過。
        - 回答簡潔，重點放前面；列多筆資料時用項目清單。

        巡檢報告格式（使用者要求巡檢時使用）：依序用「## 總結」「## 健康狀態」「## 例外與錯誤」「## 排程與備份」
        「## 資源用量」「## 帳號與安全」「## 建議處理」七個段落；沒有工具可查的段落寫「目前無法查詢」。
        總結用一兩句話說出整體狀況與最需要處理的事。
        """;

    /// <summary>分派說明：只回英文代碼。</summary>
    public const string DefaultRouterInstructions = """
        你負責判斷管理員這則訊息需要哪些領域的專家處理。只回覆以逗號分隔的英文代碼，不要任何其他文字。

        - accounts：使用者帳號、角色、權限、登入、鎖定、2FA、稽核紀錄
        - monitoring：系統健康、日誌、日誌等級、系統例外、資料庫用量、測試信與測試通知
        - ai：AI 提示詞、AI 對話紀錄、Token 用量、AI 費用與用量上限
        - ops：系統參數、公告、排程作業、系統備份、資料保留與清理
        - patrol：使用者要求整體巡檢或「看一下系統狀況」這類全面檢查

        可以複選，例如「ops, monitoring」。訊息是接續前文的（例如「那就停用他」），依最近的對話判斷。
        真的判斷不出來時回覆 monitoring, ops。
        """;

    /// <summary>帳號與權限專家。</summary>
    public const string DefaultAccountsInstructions = """
        負責使用者帳號、角色與權限、稽核紀錄。
        - 指稱帳號時一律寫出帳號（Account），不要只寫顯示名稱。
        - 停用、刪除、取消管理員這類動作影響大，執行前先列出對象並說明影響。
        - 不能對自己強制登出、重設 2FA 或取消管理員身分；系統至少要保留一位管理員。
        """;

    /// <summary>監控與診斷專家。</summary>
    public const string DefaultMonitoringInstructions = """
        負責系統健康、日誌、日誌等級、系統例外與資料庫用量。
        - 系統健康報告會實際做一次 AI 探測並登入 SMTP（會花一點 AI 費用），需要時才查，不要重複查。
        - 查日誌時先用較小的範圍（例如最近 24 小時、WARN 以上），需要再放大。
        - 分析例外時說明可能原因與建議的下一步，並附上例外編號方便到「系統例外紀錄」頁查看。
        """;

    /// <summary>AI 管理專家。</summary>
    public const string DefaultAiInstructions = """
        負責 AI 提示詞、AI 對話紀錄、Token 用量與用量上限。
        - 金額一律寫明幣別（新台幣）；用量說明時附上統計區間。
        - AI 對話紀錄的內容可能很長，只摘錄與問題相關的部分。
        """;

    /// <summary>系統設定與維運專家。</summary>
    public const string DefaultOpsInstructions = """
        負責系統參數、公告、排程作業、系統備份、資料保留與清理。
        - 回報排程狀況時列出：是否啟用、上次執行結果與時間、下次執行時間，失敗或逾期的放最前面。
        - 排程失敗時，用執行紀錄的 TraceId 去查例外與日誌找出原因。
        - 立即執行排程或備份只是排入佇列，完成後要再查執行紀錄才知道結果。
        """;
}
