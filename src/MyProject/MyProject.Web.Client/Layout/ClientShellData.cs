using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// 外框（<see cref="ClientMainLayout"/>）要的系統資訊與選單（0.10.35 起）。
/// <list type="bullet">
/// <item><see cref="LoadAsync"/>：兩個請求同時送出（以前一個接一個）。</item>
/// <item>登入成功時，登入頁先以 <see cref="PrepareAsync"/> 讀好（<c>ClientSession.EnterAfterSignInAsync</c>），
/// 外框以 <see cref="TakePrepared"/> 直接用，不必再確認一次登入、也不顯示「正在確認登入狀態…」。</item>
/// <item>先讀好的只交出一次：之後再建立的外框（例如從匿名頁回來）照常自己確認登入、自己讀。</item>
/// </list>
/// </summary>
public sealed class ClientShellData(ApiClient api)
{
    private Snapshot? prepared;

    /// <summary>讀到的系統資訊與選單（讀不到的那一項為 null）。</summary>
    public sealed record Snapshot(ShellInfoDto? Info, ShellMenuDto? Menu);

    /// <summary>同時讀系統資訊與選單。</summary>
    public async Task<Snapshot> LoadAsync()
    {
        var info = api.GetAsync<ShellInfoDto>("api/v1/shell/info");
        var menu = api.GetAsync<ShellMenuDto>("api/v1/shell/menu");
        await Task.WhenAll(info, menu);
        return new Snapshot((await info).Data, (await menu).Data);
    }

    /// <summary>登入頁在換頁前先讀好，給接下來的外框用。</summary>
    public async Task PrepareAsync() => prepared = await LoadAsync();

    /// <summary>拿登入頁先讀好的資料（只能拿一次）；沒有就回 null。</summary>
    public Snapshot? TakePrepared()
    {
        var snapshot = prepared;
        prepared = null;
        return snapshot;
    }
}
