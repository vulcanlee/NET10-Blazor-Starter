using MyProject.AccessDatas.Models;
using MyProject.Dtos.Admins;
using MyProject.Web.Ai;
using MyProject.Web.Client.Views.Admins;

namespace MyProject.Tests.Client;

/// <summary>
/// 「AI 提示詞」頁的左側分組清單（0.10.21 起取代頁籤）：依伺服器帶來的標題的「／」分組，
/// 沒有「／」的歸「一般」；組與項目都照伺服器給的順序。
/// </summary>
public sealed class PromptTemplateGroupingTests
{
    [Fact]
    public void RealTitles_ShouldBeGroupedIntoFourGroupsInServerOrder()
    {
        // 和 PromptTemplatesController 一樣的順序與標題。
        var templates = PromptTemplateKeys.All
            .Select(key => new PromptTemplateDto { Key = key, Title = PromptTemplateTitles.Of(key) })
            .ToList();

        var groups = PromptTemplateView.GroupsOf(templates);

        Assert.Equal(["一般", "狀態報告", "審查", "管理助理"], groups.Select(g => g.Name));
        Assert.Equal(["日誌分析", "例外分析", "Agent 測試台"], groups[0].Items.Select(i => i.Label));
        Assert.Equal(["蒐集", "撰寫", "收尾"], groups[1].Items.Select(i => i.Label));
        Assert.Equal(["需求完整性", "技術可行性", "使用者體驗", "彙整"], groups[2].Items.Select(i => i.Label));
        Assert.Equal(["共同", "分派", "帳號與權限", "監控與診斷", "AI 管理", "系統設定與維運"], groups[3].Items.Select(i => i.Label));

        // 每個種類剛好出現一次，鍵照原本的順序。
        Assert.Equal(PromptTemplateKeys.All, groups.SelectMany(g => g.Items).Select(i => i.Key));
    }

    [Fact]
    public void GroupOrder_ShouldFollowFirstAppearance()
    {
        var templates = new List<PromptTemplateDto>
        {
            new() { Key = "a", Title = "乙／一" },
            new() { Key = "b", Title = "單獨" },
            new() { Key = "c", Title = "乙／二" },
        };

        var groups = PromptTemplateView.GroupsOf(templates);

        Assert.Equal(["乙", "一般"], groups.Select(g => g.Name));
        Assert.Equal(["a", "c"], groups[0].Items.Select(i => i.Key));
        Assert.Equal(["b"], groups[1].Items.Select(i => i.Key));
    }
}
