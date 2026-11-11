using MyProject.Models.Systems;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 一輪對話用的 Agent（0.9.155 起，系統管理助理）：這一輪的定義（工具）、已組好的系統提示詞（含固定規則）與畫面標籤。
/// </summary>
/// <param name="Definition">這一輪的定義。<see cref="AiAgentDefinition.Name"/> 必須等於 <see cref="IAiAgentTurnPlanner.Home"/> 的名稱。</param>
/// <param name="Instructions">實際送出的系統提示詞（呼叫端已接上 <see cref="AiPromptGuardrails"/>）。</param>
/// <param name="Label">這一輪 AI 回覆在畫面上標示的「由誰回答」；null 不標示。</param>
/// <param name="Key">
/// 規劃器自己認得的這一輪代碼（0.10.7 起），跟著對話存起來（<see cref="AiAgentConversationContent.TurnKey"/>），
/// 接續時交給 <see cref="IAiAgentTurnPlanner.RestoreTurnAsync"/> 重建同一輪；null＝不必還原（開對話那一輪）。
/// </param>
public sealed record AiAgentTurn(AiAgentDefinition Definition, string Instructions, string? Label, string? Key = null);

/// <summary>
/// 每輪規劃器（0.9.155 起）：每次送出使用者訊息之前，決定這一輪要用哪一組定義、提示詞與標籤。
///
/// <para>
/// 同一段對話共用一個 MAF session（前後文延續），每一輪重新建立 agent。
/// 停下來等核准之後的回覆<b>不</b>重新規劃，沿用提出核准時的那一輪。
/// </para>
/// </summary>
public interface IAiAgentTurnPlanner
{
    /// <summary>開對話與接續時使用的定義（含全部工具，接續時才找得到待核准的工具）；也是存檔的 AgentName。</summary>
    AiAgentDefinition Home { get; }

    /// <summary>開對話與接續時的那一輪（不呼叫分派模型）。</summary>
    Task<AiAgentTurn> HomeTurnAsync(CancellationToken cancellationToken);

    /// <summary>每次送出訊息前呼叫。⚠️ 不丟例外（取消除外）：判斷失敗時自己退回預設的一輪。</summary>
    Task<AiAgentTurn> PlanAsync(
        Guid conversationId,
        string message,
        IReadOnlyList<AiAgentTranscriptEntry> transcript,
        CancellationToken cancellationToken);

    /// <summary>
    /// 接續對話時重建上次那一輪（0.10.7 起，WASM 遷移 M8）：<paramref name="key"/> 是 <see cref="AiAgentTurn.Key"/> 存下來的值，不呼叫分派模型。
    /// WASM 版每個請求都重新接續，停在核准的對話要用提出核准的那一輪繼續（Server 版同一個對話物件本來就沿用）。
    /// 預設退回 <see cref="HomeTurnAsync"/>。
    /// </summary>
    Task<AiAgentTurn> RestoreTurnAsync(string key, CancellationToken cancellationToken) => HomeTurnAsync(cancellationToken);
}
