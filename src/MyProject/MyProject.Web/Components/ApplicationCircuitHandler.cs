using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MyProject.Web.Components;

/// <summary>
/// 紀錄 Blazor Server circuit 生命週期（開啟 / 連線起落 / 關閉）與**使用者的畫面切換軌跡**。
///
/// 導覽日誌集中在這裡而非 MainLayout，是因為 CircuitHandler 涵蓋所有版面 ——
/// 包含繞過 MainLayout 的 EmptyLayout（啟動畫面）與 NoFooterLayout（登入頁）。
/// 這樣不需要動到任何頁面就能追蹤完整的使用者軌跡。
///
/// 身分取自 AuthenticationStateProvider 而**不是** CurrentUserService：後者要等
/// AuthenticationStateHelper.Check() 在元件的 OnInitializedAsync 跑過才有值，而首頁走
/// EmptyLayout 根本不會跑到，會導致大部分 circuit 都記成匿名。CircuitFactory 的順序是
/// 建立 scope → 初始化 NavigationManager → 設定 AuthenticationState → 才解析 CircuitHandler，
/// 所以這裡取得的驗證狀態是可靠的。
///
/// **注意 claim 對應在兩套驗證機制中是相反的**：
///   Cookie（本頁適用）：Sid=UserId、NameIdentifier=Account、Name=**姓名（個資，絕不記錄）**
///   JWT（API 適用）：  NameIdentifier=UserId、Name=Account
/// 在此誤用 ClaimTypes.Name 會把使用者姓名寫進日誌。
/// </summary>
public sealed class ApplicationCircuitHandler : CircuitHandler, IDisposable
{
    private const string AnonymousAccount = "(anonymous)";

    private readonly ILogger<ApplicationCircuitHandler> logger;
    private readonly NavigationManager navigationManager;
    private readonly AuthenticationStateProvider authenticationStateProvider;

    private string circuitId = string.Empty;
    private string previousPath = string.Empty;
    private string account = AnonymousAccount;
    private int userId;
    private bool subscribed;

    public ApplicationCircuitHandler(
        ILogger<ApplicationCircuitHandler> logger,
        NavigationManager navigationManager,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.logger = logger;
        this.navigationManager = navigationManager;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        circuitId = circuit.Id;
        previousPath = ToRelativePath(navigationManager.Uri);
        await ResolveIdentityAsync();

        navigationManager.LocationChanged += OnLocationChanged;
        subscribed = true;

        // 進入點記在這裡：circuit 開啟時第一個頁面已經載入，不會再觸發 LocationChanged。
        logger.LogInformation(
            "Blazor circuit opened. CircuitId={CircuitId}, EntryPath={EntryPath}, Account={Account}, UserId={UserId}",
            circuitId, previousPath, account, userId);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var (account, userId) = ResolveUser();
        logger.LogDebug(
            "Blazor circuit connection up. CircuitId={CircuitId}, Account={Account}, UserId={UserId}",
            circuit.Id, account, userId);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var (account, userId) = ResolveUser();
        logger.LogWarning(
            "Blazor circuit connection down (client may have disconnected). CircuitId={CircuitId}, Account={Account}, UserId={UserId}, LastPath={LastPath}",
            circuit.Id, account, userId, previousPath);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Unsubscribe();

        var (account, userId) = ResolveUser();
        logger.LogInformation(
            "Blazor circuit closed. CircuitId={CircuitId}, Account={Account}, UserId={UserId}, LastPath={LastPath}",
            circuit.Id, account, userId, previousPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 使用者切換畫面。這是「使用者當時做了什麼」最基本的一條軌跡，因此記在 Information。
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        var path = ToRelativePath(args.Location);
        var (account, userId) = ResolveUser();

        logger.LogInformation(
            "User navigated. Path={Path}, PreviousPath={PreviousPath}, Account={Account}, UserId={UserId}, CircuitId={CircuitId}",
            path, previousPath, account, userId, circuitId);

        previousPath = path;
    }

    /// <summary>
    /// 只取 Account 與 UserId —— 姓名、Email 等個資不進日誌。
    /// 註：Google SSO 使用者的 Account 本身就是 Email，這是已知且已接受的取捨。
    /// </summary>
    private (string Account, int UserId) ResolveUser() => (account, userId);

    private async Task ResolveIdentityAsync()
    {
        try
        {
            var state = await authenticationStateProvider.GetAuthenticationStateAsync();
            var principal = state.User;
            if (principal.Identity?.IsAuthenticated != true)
            {
                return;
            }

            // Cookie 驗證的對應：NameIdentifier=帳號、Sid=使用者編號。
            // ClaimTypes.Name 在這裡是「姓名」，屬個資，刻意不取用。
            account = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? AnonymousAccount;
            userId = int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var id) ? id : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve identity for circuit. CircuitId={CircuitId}", circuitId);
        }
    }

    /// <summary>只記路徑，不記 query string —— 它可能挾帶使用者輸入或敏感參數。</summary>
    private string ToRelativePath(string uri)
    {
        try
        {
            var relative = navigationManager.ToBaseRelativePath(uri);
            var queryIndex = relative.IndexOf('?');
            if (queryIndex >= 0)
            {
                relative = relative[..queryIndex];
            }

            return string.IsNullOrEmpty(relative) ? "/" : "/" + relative;
        }
        catch (ArgumentException)
        {
            // ToBaseRelativePath 對不屬於本站的 URI 會丟例外；導覽日誌不該因此中斷。
            return "(external)";
        }
    }

    private void Unsubscribe()
    {
        if (subscribed == false)
        {
            return;
        }

        navigationManager.LocationChanged -= OnLocationChanged;
        subscribed = false;
    }

    /// <summary>
    /// 解除事件訂閱。CircuitHandler 是 Scoped、生命週期等同 circuit，但仍必須解除 ——
    /// LogLevelRuntimeState 先前就是因為漏了這步而造成跨測試污染。
    /// </summary>
    public void Dispose() => Unsubscribe();
}
