using MyProject.Business.Helpers;
using MyProject.Models.Systems;

namespace MyProject.Tests;

public sealed class EmailTemplatesTests
{
    /// <summary>放進 HTML 的變數一律要編碼，否則系統名稱或帳號裡的標籤會變成信件內的 HTML。</summary>
    [Fact]
    public void BuildTest_ShouldHtmlEncodeVariables()
    {
        var message = EmailTemplates.BuildTest("alice@example.com", "<b>A&B</b>", DateTime.Now, "Pickup");

        Assert.DoesNotContain("<b>A&B</b>", message.HtmlBody);
        Assert.Contains("&lt;b&gt;A&amp;B&lt;/b&gt;", message.HtmlBody);
        Assert.Contains("<b>A&B</b>", message.TextBody);
    }

    [Fact]
    public void BuildTest_ShouldPrefixSubjectWithSystemName()
    {
        var message = EmailTemplates.BuildTest("alice@example.com", "企業管理平台", DateTime.Now, "Smtp");

        Assert.StartsWith("[企業管理平台]", message.Subject);
        Assert.Equal("alice@example.com", message.To);
        Assert.Equal(EmailKinds.Test, message.Kind);
    }
}
