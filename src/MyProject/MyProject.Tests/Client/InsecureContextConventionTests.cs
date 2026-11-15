namespace MyProject.Tests.Client;

/// <summary>
/// 0.10.31 起：用 http（非 localhost）開也能啟動。
/// .NET 11 的 dotnet.js 啟動時無條件檢查 <c>crypto.subtle</c>，瀏覽器只在安全連線（https、localhost）提供它，
/// 所以 http 加 IP 或網域開會停在載入卡片（Failed to start platform）。js/insecure-context-crypto.js 在 http 時補上
/// 執行環境唯一用到的 <c>crypto.subtle.digest('SHA-256')</c>；它必須在 blazor.webassembly 之前載入，而且 https 時不動作。
/// </summary>
public class InsecureContextConventionTests
{
    private static string WebRoot => SourceTree.ClientWebRoot;

    [Fact]
    public void IndexHtml_ShouldLoadTheCryptoShim_BeforeBlazorStarts()
    {
        var html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));
        var shim = html.IndexOf("<script src=\"js/insecure-context-crypto.js\"></script>", StringComparison.Ordinal);
        var blazor = html.IndexOf("<script src=\"_framework/blazor.webassembly", StringComparison.Ordinal);

        Assert.True(shim >= 0, "index.html 沒有載入 js/insecure-context-crypto.js（http 開會卡在載入卡片）。");
        Assert.True(blazor > shim, "js/insecure-context-crypto.js 要排在 blazor.webassembly 之前，執行環境啟動時就要有 crypto.subtle。");
    }

    [Fact]
    public void CryptoShim_ShouldOnlyActOnInsecureContexts()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot, "js", "insecure-context-crypto.js"));

        Assert.Contains("window.isSecureContext", script, StringComparison.Ordinal);
        Assert.Contains("crypto.subtle", script, StringComparison.Ordinal);
        Assert.Contains("SHA-256", script, StringComparison.Ordinal);
    }
}
