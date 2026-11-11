using System.Globalization;
using System.Text;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;

namespace MyProject.Web.Ai;

/// <summary>
/// AI 例外分析的對話檢查與稽核摘要（0.10.5 起，WASM 遷移 M6）。
///
/// 對話由畫面送回（伺服器無狀態，不怕 IIS 回收），但第一則例外明細與 system 提示一律由伺服器重組，
/// 所以畫面只能送「之後的」問答：這裡擋下格式不對、太長或超過追問上限的對話，不讓它送到上游計費。
/// </summary>
public static class AiExceptionConversation
{
    /// <inheritdoc cref="ExceptionAiRequestDto.MaxQuestionLength"/>
    public const int MaxQuestionLength = ExceptionAiRequestDto.MaxQuestionLength;

    /// <inheritdoc cref="ExceptionAiRequestDto.MaxReplyLength"/>
    public const int MaxReplyLength = ExceptionAiRequestDto.MaxReplyLength;

    /// <summary>
    /// 檢查例外明細之後的對話：AI 回覆與追問交替（以 AI 回覆開頭），追問不空白、不超過 <see cref="MaxQuestionLength"/>，
    /// 回覆不超過 <see cref="MaxReplyLength"/>，追問輪數不超過 <paramref name="maxFollowUpRounds"/>。
    /// <paramref name="endsWithQuestion"/> 為 true（送給 AI）時最後一則必須是追問（第一次分析可以是空的）；
    /// false（匯出 PDF）時至少要有一則、以 AI 回覆結尾。通過回 null，否則回給使用者看的原因。
    /// </summary>
    public static string? Validate(IReadOnlyList<AiChatMessageDto>? messages, int maxFollowUpRounds, bool endsWithQuestion)
    {
        messages ??= [];
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var expected = index % 2 == 0 ? AiChatRoles.Assistant : AiChatRoles.User;
            if (!string.Equals(message.Role, expected, StringComparison.Ordinal))
            {
                return "對話格式不正確：例外明細之後必須是 AI 回覆與追問交替。";
            }

            var content = message.Content ?? string.Empty;
            if (expected == AiChatRoles.User)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    return "追問內容不可空白。";
                }

                if (content.Length > MaxQuestionLength)
                {
                    return $"單則追問不可超過 {MaxQuestionLength} 字。";
                }
            }
            else if (content.Length > MaxReplyLength)
            {
                return "AI 回覆內容異常龐大，無法送出。";
            }
        }

        if (endsWithQuestion ? messages.Count % 2 != 0 : messages.Count == 0 || messages.Count % 2 == 0)
        {
            return endsWithQuestion ? "對話格式不正確：最後一則必須是追問。" : "對話格式不正確：沒有可匯出的 AI 回覆。";
        }

        var followUps = messages.Count(message => message.Role == AiChatRoles.User);
        if (followUps > maxFollowUpRounds)
        {
            return $"已達追問上限（{maxFollowUpRounds} 輪）。請關閉視窗後重新分析，或調整 AiSettings:MaxFollowUpRounds。";
        }

        return null;
    }

    /// <summary>
    /// 稽核摘要（格式與 Server 版對話窗相同）。數字依伺服器組出的完整對話（含第一則例外明細、不含 system）計算。
    ///
    /// ⚠️ 只寫輪次、數量、模型、用量與耗時，<b>絕不</b>寫入例外內容、追問或 AI 輸出：AuditLog 的可視範圍比本頁更廣（見速查 §6.5）。
    /// </summary>
    public static string BuildAuditDetail(int round, int messageCount, int characterCount, AiAnalysisResult? result)
    {
        var builder = new StringBuilder();
        builder.Append(round == 0 ? "初始分析" : $"追問第 {round} 輪")
            .Append("；對話 ").Append(messageCount).Append(" 則、")
            .Append(characterCount).Append(" 字元");

        if (result is null)
        {
            return builder.ToString();
        }

        if (string.IsNullOrWhiteSpace(result.ModelName) == false)
        {
            builder.Append("；模型 ").Append(result.ModelName);
        }

        var usage = AiTokenUsage.ToDisplayText(result.Usage);
        if (string.IsNullOrEmpty(usage) == false)
        {
            builder.Append("；用量 ").Append(usage);
        }

        builder.Append("；耗時 ").Append(result.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms");

        if (result.Success == false)
        {
            builder.Append("；失敗原因 ").Append(result.Reason);
        }

        return builder.ToString();
    }
}
