using System.Globalization;
using MyProject.Dtos.Admins;

namespace MyProject.Web.Configuration.Parameters;

/// <summary>
/// 系統參數值的解析與格式化（0.9.98 起）。存進資料庫與設定的一律是不變文化的字串：
/// 整數 <c>30</c>、布林 <c>true</c>／<c>false</c>、文字去頭尾空白。
/// 0.10.6 起放在 Client（命名空間不變）：伺服器（參數目錄、存檔、管理助理）與 WASM 的系統參數頁共用同一份，
/// 定義以 <see cref="ISystemParameterFormat"/> 傳入。
/// </summary>
public static class SystemParameterValueCodec
{
    /// <summary>把輸入正規化成要存的字串；不合法時回 false 與可以直接顯示給管理員的訊息。</summary>
    public static bool TryNormalize(ISystemParameterFormat definition, string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var text = raw?.Trim() ?? string.Empty;

        switch (definition.Kind)
        {
            case SystemParameterKind.Int:
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    error = "請輸入整數。";
                    return false;
                }

                if ((definition.Min is { } min && number < min) || (definition.Max is { } max && number > max))
                {
                    error = $"必須介於 {definition.Min:N0} 到 {definition.Max:N0} 之間。";
                    return false;
                }

                normalized = number.ToString(CultureInfo.InvariantCulture);
                return true;

            case SystemParameterKind.Bool:
                if (!bool.TryParse(text, out var flag))
                {
                    error = "只接受 true 或 false。";
                    return false;
                }

                normalized = flag ? "true" : "false";
                return true;

            default:
                if (definition.Required && text.Length == 0)
                {
                    error = "不可留空。";
                    return false;
                }

                // 控制字元（含換行）會進到信件標題與頁面標題，一律拒絕。
                if (text.Any(char.IsControl))
                {
                    error = "不可包含換行或控制字元。";
                    return false;
                }

                if (definition.MaxLength is { } maxLength && text.Length > maxLength)
                {
                    error = $"最多 {maxLength} 個字。";
                    return false;
                }

                if (definition.AllowedValues is { } allowed && text.Length > 0)
                {
                    var match = allowed.FirstOrDefault(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        error = $"只接受空白或 {string.Join("、", allowed)}。";
                        return false;
                    }

                    text = match;
                }

                normalized = text;
                return true;
        }
    }

    /// <summary>比較用：設定檔裡的布林可能是 <c>True</c>，數字可能有前導零。無法解析就原樣回傳。</summary>
    public static string? Canonical(ISystemParameterFormat definition, string? value)
    {
        if (value is null)
        {
            return null;
        }

        return definition.Kind switch
        {
            SystemParameterKind.Int when int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                => n.ToString(CultureInfo.InvariantCulture),
            SystemParameterKind.Bool when bool.TryParse(value.Trim(), out var b) => b ? "true" : "false",
            SystemParameterKind.String when definition.AllowedValues is { } allowed
                => allowed.FirstOrDefault(x => string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim(),
            SystemParameterKind.String => value.Trim(),
            _ => value,
        };
    }

    /// <summary>給人看的值：<c>30 天</c>、<c>0（不自動清除）</c>、<c>開啟</c>、<c>（空白）</c>。</summary>
    public static string Display(ISystemParameterFormat definition, string? value)
    {
        if (value is null)
        {
            return "（未設定）";
        }

        var canonical = Canonical(definition, value) ?? value;
        switch (definition.Kind)
        {
            case SystemParameterKind.Bool:
                return canonical == "true" ? "開啟" : canonical == "false" ? "關閉" : canonical;
            case SystemParameterKind.Int:
                if (canonical == "0" && definition.ZeroMeaning is { } zeroMeaning)
                {
                    return $"0（{zeroMeaning}）";
                }

                return int.TryParse(canonical, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                    ? $"{n.ToString("N0", CultureInfo.InvariantCulture)}{(definition.Unit is null ? string.Empty : " " + definition.Unit)}"
                    : canonical;
            default:
                if (canonical.Length == 0)
                {
                    return definition.BlankMeaning is { } blankMeaning ? $"（空白：{blankMeaning}）" : "（空白）";
                }

                return canonical;
        }
    }

    /// <summary>新的天數比目前短（0＝不清除，改成任何正數都算縮短）。0.10.6 起從系統參數頁搬來，頁面與管理助理共用。</summary>
    public static bool ShortensRetention(string? currentValue, string? newValue)
    {
        if (!int.TryParse(newValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var next) || next <= 0)
        {
            return false;
        }

        if (!int.TryParse(currentValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var current))
        {
            return false;
        }

        return current == 0 || next < current;
    }

    /// <summary>範圍說明：<c>1～3,650 天（0＝不自動清除）</c>、<c>最多 50 個字，不可留空</c>；沒有範圍時回 null。</summary>
    public static string? RangeText(ISystemParameterFormat definition)
    {
        if (definition.Kind == SystemParameterKind.Int && definition.Min is { } min && definition.Max is { } max)
        {
            var text = $"{min.ToString("N0", CultureInfo.InvariantCulture)}～{max.ToString("N0", CultureInfo.InvariantCulture)}{(definition.Unit is null ? string.Empty : " " + definition.Unit)}";
            return definition.ZeroMeaning is { } zero ? $"{text}（0＝{zero}）" : text;
        }

        if (definition.Kind == SystemParameterKind.String && definition.MaxLength is { } maxLength)
        {
            return $"最多 {maxLength} 個字{(definition.Required ? "，不可留空" : "，可以留空")}";
        }

        return null;
    }
}
