using MyProject.Models.AdapterModel;

namespace MyProject.Web.Ai;

/// <summary>
/// AI 對話紀錄的顯示文字（0.10.5 起放在 Client，命名空間不變）：WASM 頁面、伺服器的 PDF 與 CSV 共用同一份，
/// 畫面、PDF、CSV 的「結果」與角色名稱才不會分歧（原本在 <c>AiCallLogPdfBuilder</c>）。
/// </summary>
public static class AiCallLogText
{
    /// <summary>角色的顯示名稱。</summary>
    public static string RoleLabel(string role) => role switch
    {
        "system" => "系統提示（System）",
        "user" => "使用者（User）",
        "assistant" => "AI 助理（Assistant）",

        // Agent 的工具執行結果（0.9.149 起 Responses 的 function_call_output 解析成 tool）。
        "tool" => "工具結果（Tool）",
        _ => string.IsNullOrWhiteSpace(role) ? "（未標示角色）" : role,
    };

    /// <summary>結果文字。</summary>
    public static string DescribeOutcome(AiCallLogAdapterModel item)
    {
        if (item.Success)
        {
            return "成功";
        }

        if (item.IsCanceled)
        {
            return "已取消";
        }

        return string.IsNullOrWhiteSpace(item.FailureReason) ? "失敗" : $"失敗（{item.FailureReason}）";
    }
}
