using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;

namespace MyProject.Business.Helpers;

/// <summary>
/// 存檔時檢查下拉選項仍然有效（0.10.15 起，畫面選項資料一致性）：畫面上的選項是開窗時讀的，別人可能在這之間刪除或停用了那個分類、團隊。
/// 只檢查「這次新選的」值 —— 原本就有、這次沒動的值不擋（舊資料只改其他欄位時照常可存，例如分類後來被停用）。
/// 比對規則與團隊改名連動相同（<see cref="TeamHierarchy"/>）：去前後空白、不分大小寫。
/// 放在服務的 BeforeAddCheckAsync／BeforeUpdateCheckAsync（畫面與 API 存檔前呼叫），AI 工具直接新增時不受影響。
/// </summary>
public static class SelectedOptionGuard
{
    /// <summary>這次新選、卻不在 <paramref name="available"/> 裡的第一個值；都有效時回 null。</summary>
    public static string? FindUnavailable(IEnumerable<string>? selected, IEnumerable<string>? previous, IEnumerable<string> available)
    {
        var before = new HashSet<string>((previous ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(available.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return (selected ?? [])
            .Select(Normalize)
            .Where(x => x.Length > 0)
            .FirstOrDefault(x => !before.Contains(x) && !allowed.Contains(x));
    }

    /// <summary>給使用者的訊息：畫面收到失敗後會重讀選項。</summary>
    public static string Message(string label, string name) => $"所選的{label}「{name}」已不存在或已停用，請重新選擇（選項已更新）。";

    /// <summary>目前啟用、未刪除的團隊名稱（與各頁「團隊」下拉的來源 <c>TeamService.GetAllEnabledNamesAsync</c> 相同）。</summary>
    public static Task<List<string>> EnabledTeamNamesAsync(BackendDBContext context)
        => context.Team.AsNoTracking().Where(x => x.IsEnabled).Select(x => x.Name).ToListAsync();

    /// <summary>目前啟用、未刪除的分類名稱（專案的「分類」下拉）。團隊可見性不在這裡過濾：看不到的分類本來就選不到。</summary>
    public static Task<List<string>> EnabledCategoryNamesAsync(BackendDBContext context)
        => context.Category.AsNoTracking().Where(x => x.IsEnabled).Select(x => x.Name).ToListAsync();

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
