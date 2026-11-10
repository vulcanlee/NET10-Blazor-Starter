using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace MyProject.Web.Client;

/// <summary>
/// 哪些路由已經搬到 WASM（0.10.1 起，WASM 遷移 M2）：本組件裡所有頁面的 <c>@page</c>。
/// 主機（<c>App.razor.cs</c>）據此決定整頁請求用 WebAssembly 還是 Server 互動模式；
/// 兩邊的路由器據此判斷「這個網址不歸我管」，改成整頁載入，交給另一邊。
/// 遷移期間只支援沒有參數的路由（M2～M9 的頁面都是）。
/// </summary>
public static class ClientRouteTable
{
    private static readonly Lazy<HashSet<string>> routes = new(() =>
        typeof(ClientRouteTable).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => Normalize(attribute.Template))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>已搬到 WASM 的路由（正規化後：去掉前後斜線、query 與錨點）。</summary>
    public static IReadOnlyCollection<string> Routes => routes.Value;

    /// <summary>這個路徑（可含 query 與錨點，大小寫不拘）是不是 WASM 頁面。</summary>
    public static bool IsClientRoute(string? path) => routes.Value.Contains(Normalize(path));

    /// <summary>
    /// 去掉 query 與錨點、解開百分比編碼、去掉前後的空白與斜線。⚠️ 主機（已解碼的 <c>Request.Path</c>）與 WASM 路由器（未解碼的網址）
    /// 必須得到同一個結果，否則 <c>/%50rofile</c> 這種網址會被兩邊互相踢來踢去，無限重新載入。
    /// </summary>
    internal static string Normalize(string? path)
    {
        var withoutQuery = (path ?? string.Empty).Split('?', '#')[0];
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(withoutQuery);
        }
        catch (UriFormatException)
        {
            decoded = withoutQuery;
        }

        return decoded.Trim().Trim('/');
    }
}
