using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Web.Client.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests.Client;

/// <summary>
/// 前端錯誤回報（0.10.36 起，日誌與例外處理 PRD LOG-23）：js/client-error-reporter.js 是唯一的回報管線，
/// 排在所有腳本之前（WASM 啟動失敗也接得到）；頁面錯誤只換掉內容區、換頁時復原；錯誤邊界以外的未處理例外也回報。
/// </summary>
public sealed class ClientErrorReportingConventionTests
{
    private static string IndexHtml => File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "index.html"));

    private static string Reporter => File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "client-error-reporter.js"));

    [Fact]
    public void Reporter_ShouldLoadBeforeEveryOtherScript_AndBlazorShouldStartManually()
    {
        var html = IndexHtml;
        var scripts = Regex.Matches(html, "<script src=\"(?<src>[^\"]+)\"(?<attributes>[^>]*)>").Select(x => x.Groups["src"].Value).ToList();
        var bodyScripts = scripts.SkipWhile(x => x != "js/client-error-reporter.js").ToList();

        Assert.Equal("js/client-error-reporter.js", bodyScripts.FirstOrDefault());
        Assert.Equal(new[] { "js/loading-hint.js" }, scripts.TakeWhile(x => x != "js/client-error-reporter.js"));   // head 裡只有載入卡片的提示
        Assert.Equal("js/app-start.js", scripts.Last());
        Assert.Matches(new Regex("<script src=\"_framework/blazor\\.webassembly[^\"]*\" autostart=\"false\"></script>"), html);

        var start = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "app-start.js"));
        Assert.Contains("window.Blazor.start().then(", start, StringComparison.Ordinal);
        Assert.Contains("window.appClientErrors.started();", start, StringComparison.Ordinal);
        Assert.Contains("window.appClientErrors.report('boot', ", start, StringComparison.Ordinal);
    }

    /// <summary>資源載入失敗的 error 事件不會冒泡，只有 capture 階段收得到；CSP 違規另有事件。</summary>
    [Fact]
    public void Reporter_ShouldListenInTheCapturePhase_AndForSecurityPolicyViolations()
    {
        var reporter = Reporter;

        Assert.Matches(new Regex(@"window\.addEventListener\('error', event => \{[\s\S]*?\}, true\);"), reporter);
        Assert.Contains("document.addEventListener('securitypolicyviolation', cspViolated);", reporter, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('unhandledrejection', ", reporter, StringComparison.Ordinal);
        Assert.Contains("localStorage", reporter, StringComparison.Ordinal);
        Assert.Contains("sender.invokeMethodAsync('SendAsync', ", reporter, StringComparison.Ordinal);
    }

    /// <summary>追蹤碼在瀏覽器（JS、.NET）產生、伺服器沿用：三處的字元集必須相同，否則伺服器會判定格式不對而換掉。</summary>
    [Fact]
    public void TraceCodeAlphabet_ShouldBeTheSameEverywhere()
    {
        var server = (string)typeof(TraceCode).GetField("Alphabet", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var script = Regex.Match(Reporter, "const traceCodeAlphabet = '(?<value>[^']+)';").Groups["value"].Value;

        Assert.Equal(ClientErrorCapture.TraceCodeAlphabet, server);
        Assert.Equal(server, script);
        Assert.True(TraceCode.IsValid(ClientErrorCapture.NewTraceCode()));
    }

    /// <summary>頁面出錯時只換掉內容區（側邊選單還在），換頁時兩層錯誤邊界都復原。</summary>
    [Fact]
    public void PageErrorBoundaries_ShouldWrapTheBody_AndRecoverOnNavigation()
    {
        var layout = File.ReadAllText(Path.Combine(SourceTree.Client, "Layout", "ClientMainLayout.razor"));
        var layoutCode = File.ReadAllText(Path.Combine(SourceTree.Client, "Layout", "ClientMainLayout.razor.cs"));
        var routes = File.ReadAllText(Path.Combine(SourceTree.Client, "ClientRoutes.razor"));

        Assert.Matches(new Regex(@"<ClientErrorBoundary @ref=""bodyBoundary"">\s*<ChildContent>\s*@Body\s*</ChildContent>"), layout);
        Assert.Matches(new Regex(@"private void OnLocationChanged\([\s\S]*?bodyBoundary\?\.Recover\(\);"), layoutCode);
        Assert.Matches(new Regex(@"<ClientErrorBoundary @ref=""pageBoundary"">\s*<ChildContent>\s*<RouteView "), routes);
        Assert.Matches(new Regex(@"private void OnLocationChanged\([\s\S]*?overlayBoundary\?\.Recover\(\);\s*pageBoundary\?\.Recover\(\);"), routes);
    }

    [Fact]
    public void Program_ShouldWireTheErrorCapture()
    {
        var program = File.ReadAllText(Path.Combine(SourceTree.Client, "Program.cs"));

        Assert.Contains("builder.Services.AddSingleton<ClientErrorCapture>();", program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<ILoggerProvider, ClientFrameworkErrorLoggerProvider>();", program, StringComparison.Ordinal);
        Assert.Contains("ViewNotification.UseErrorCapture(exception => errorCapture.Capture(\"caught\", exception));", program, StringComparison.Ordinal);
    }

    /// <summary>同一個例外只回報一次（錯誤邊界與框架日誌可能先後看到同一個），追蹤碼記在例外上給錯誤畫面用。</summary>
    [Fact]
    public void Capture_ShouldReportOnce_AndAttachTheTraceCode()
    {
        var js = new ClientSessionTests.RecordingJsRuntime();
        var capture = new ClientErrorCapture(js, NullLogger<ClientErrorCapture>.Instance);
        var exception = new InvalidOperationException("boom");

        var first = capture.Capture("boundary", exception);
        var second = capture.Capture("unhandled", exception);

        Assert.Equal(first, second);
        Assert.Equal(first, ClientErrorCapture.TraceCodeOf(exception));
        var call = Assert.Single(js.Calls);
        Assert.Equal("appClientErrors.report", call.Identifier);
        Assert.Equal(new object?[] { "boundary", "boom" }, call.Args.Take(2));
        Assert.Equal(first, call.Args[4]);
    }

    /// <summary>錯誤邊界以外的未處理例外：只接框架（Microsoft.AspNetCore.Components.*）Error 以上且帶例外的日誌。</summary>
    [Fact]
    public void FrameworkErrorProvider_ShouldOnlyReportFrameworkErrorsWithAnException()
    {
        var js = new ClientSessionTests.RecordingJsRuntime();
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(js)
            .AddSingleton<ILogger<ClientErrorCapture>>(NullLogger<ClientErrorCapture>.Instance)
            .AddSingleton<ClientErrorCapture>()
            .BuildServiceProvider();
        using var provider = new ClientFrameworkErrorLoggerProvider(services);
        var renderer = provider.CreateLogger("Microsoft.AspNetCore.Components.WebAssembly.Rendering.WebAssemblyRenderer");
        var ours = provider.CreateLogger("MyProject.Web.Client.Views.Projects.ProjectView");

        renderer.LogCritical(new InvalidOperationException("render failed"), "Unhandled exception rendering component");
        renderer.LogWarning(new InvalidOperationException("warning only"), "Warning");
        renderer.LogError("No exception");
        renderer.LogError(new OperationCanceledException(), "Canceled");
        ours.LogError(new InvalidOperationException("ours"), "Handled by the page");

        var call = Assert.Single(js.Calls);
        Assert.Equal(new object?[] { "unhandled", "render failed" }, call.Args.Take(2));
    }
}
