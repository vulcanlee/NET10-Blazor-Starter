using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Commons;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Api;

/// <summary>
/// WASM 前端呼叫 Web API 的唯一入口（0.10.1 起）：自動帶存取 Token、401 時刷新後重試一次、
/// 被要求先改密碼或設定兩步驟驗證時整頁導到那兩頁、連不上伺服器時回失敗結果而不是丟例外（畫面不卡死）。
/// 回傳一律是伺服器的 <see cref="ApiResult{T}"/>。
/// </summary>
public class ApiClient
{
    /// <summary>連不上伺服器時的訊息（M12 起改由離線提示列處理）。</summary>
    public const string OfflineMessage = "目前無法連線到伺服器，請稍後再試。";

    private readonly HttpClient http;
    private readonly ClientSession session;
    private readonly NavigationManager navigation;
    private readonly ILogger<ApiClient> logger;

    public ApiClient(HttpClient http, ClientSession session, NavigationManager navigation, ILogger<ApiClient> logger)
    {
        this.http = http;
        this.session = session;
        this.navigation = navigation;
        this.logger = logger;
    }

    public Task<ApiResult<T>> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null);

    public Task<ApiResult<T>> PostAsync<T>(string url, object? body = null) => SendAsync<T>(HttpMethod.Post, url, body);

    public Task<ApiResult<T>> PutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body);

    public Task<ApiResult<T>> DeleteAsync<T>(string url) => SendAsync<T>(HttpMethod.Delete, url, null);

    /// <summary>
    /// 可能要跑很久的請求（0.10.4 起，例如稽核紀錄的完整性驗證，時間與紀錄筆數成正比）：最多等 <see cref="LongRunningTimeout"/>，
    /// 不受一般請求 100 秒的逾時限制。其他規則（自動帶 Token、401 刷新、離線）與一般請求相同。
    /// </summary>
    public Task<ApiResult<T>> PostLongRunningAsync<T>(string url, object? body = null) => SendAsync<T>(HttpMethod.Post, url, body, longRunning: true);

    /// <summary>
    /// 可以中途取消的長時間請求（0.10.5 起，例如 AI 對話紀錄的內文搜尋：逐檔掃描，離開頁面時停止）：同 <see cref="PostLongRunningAsync{T}(string, object?)"/>，
    /// <paramref name="cancellationToken"/> 取消時中斷請求（伺服器端同步停止），回失敗結果「已取消。」、不丟例外，由呼叫端依自己的取消原因處理。
    /// </summary>
    public Task<ApiResult<T>> PostLongRunningAsync<T>(string url, object? body, CancellationToken cancellationToken) => SendAsync<T>(HttpMethod.Post, url, body, longRunning: true, cancellationToken);

    /// <summary>長時間請求的上限。</summary>
    public static readonly TimeSpan LongRunningTimeout = TimeSpan.FromMinutes(30);

    private HttpClient? longRunningHttp;

    /// <summary>
    /// 最近一次失敗的 API 回應帶回的追蹤碼（0.10.2 起）：<c>ViewNotification</c> 顯示錯誤時取用一次就清掉，
    /// 使用者按「複製」的內容裡就有伺服器日誌查得到的同一個碼。WASM 只有一個使用者、單一執行緒，所以用 static。
    /// 只在失敗後 <see cref="TraceCodeLifetime"/> 內有效：沒有跳通知的失敗（例如背景讀取）留下的碼，不會被之後無關的錯誤拿去用。
    /// </summary>
    private static (string Code, long At)? lastFailure;

    internal static readonly TimeSpan TraceCodeLifetime = TimeSpan.FromSeconds(10);

    public static string? TakeLastFailureTraceId()
    {
        var taken = lastFailure;
        lastFailure = null;
        return taken is { } failure && Environment.TickCount64 - failure.At <= (long)TraceCodeLifetime.TotalMilliseconds ? failure.Code : null;
    }

    /// <summary>
    /// 下載檔案（0.10.2 起，例如匯出 Excel）：成功時回檔案內容與伺服器給的檔名；
    /// 失敗時伺服器回的是 <see cref="ApiResult{T}"/>，訊息照樣帶回（例如筆數超過上限）。
    /// </summary>
    public async Task<ApiDownload> DownloadAsync(string url, object? body = null)
    {
        try
        {
            var response = await SendWithRefreshAsync(HttpMethod.Post, url, body);
            if (response is null)
            {
                return ApiDownload.Failed("登入已失效，請重新登入。", (int)HttpStatusCode.Unauthorized);
            }

            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType != "application/json")
            {
                var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                    ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                    ?? "download";
                return new ApiDownload(true, string.Empty, await response.Content.ReadAsByteArrayAsync(), fileName,
                    response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", (int)response.StatusCode);
            }

            var result = await ReadAsync<object>(response, url);
            Remember(result);
            // 失敗原因在 ErrorMessage（Message 是「驗證失敗」之類的分類字）。
            return ApiDownload.Failed(result.UserMessage(), result.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JSException)
        {
            logger.LogWarning(ex, "API download failed because the server could not be reached. Url={Url}", url);
            return ApiDownload.Failed(OfflineMessage, 0);
        }
    }

    /// <summary>
    /// 由瀏覽器直接送出的請求（0.10.3 起，帶附件的 multipart：檔案不經過 WASM 記憶體，見 <see cref="Commons.BrowserFiles"/>）。
    /// <paramref name="send"/> 以傳入的存取 Token 送出一次；規則與一般 API 呼叫相同：401 時刷新後重送一次、
    /// 被要求先改密碼或設定兩步驟驗證時整頁導過去、連不上伺服器時回失敗結果。
    /// </summary>
    public async Task<ApiResult<T>> SendFormAsync<T>(string url, Func<string?, Task<Commons.RawResponse>> send)
    {
        try
        {
            var raw = await send(await session.GetAccessTokenAsync());
            if (raw.Status == (int)HttpStatusCode.Unauthorized)
            {
                if (!await session.RefreshAsync())
                {
                    if (session.IsThrottled)
                    {
                        return ApiResult<T>.FailureResult(ClientSession.ThrottledMessage, (int)HttpStatusCode.TooManyRequests);
                    }

                    await session.SignInAgainAsync();
                    return ApiResult<T>.UnauthorizedResult("登入已失效，請重新登入。");
                }

                raw = await send(await session.GetAccessTokenAsync());
            }

            var result = Parse<T>(raw, url);
            Remember(result);
            if (raw.Status == (int)HttpStatusCode.Forbidden)
            {
                RedirectForForcedSetup(result.ErrorCode);
            }

            return result;
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "API form request failed because the server could not be reached. Url={Url}", url);
            return ApiResult<T>.FailureResult(OfflineMessage, 0);
        }
    }

    /// <summary>
    /// NDJSON 串流（0.10.5 起，AI 分析逐字顯示）：伺服器每送出一行就呼叫一次 <paramref name="onItem"/>。
    /// 規則與一般 API 呼叫相同：自動帶 Token、401 時刷新後重送一次、被要求先改密碼或設定兩步驟驗證時整頁導過去；
    /// 伺服器拒絕（403、404、400…）時回的是一般的 <see cref="ApiResult{T}"/>，訊息照樣帶回。
    /// 只有標頭受一般請求 100 秒的逾時限制，之後的內容可以一直收（AI 第一個字可能要等好幾分鐘）。
    /// <paramref name="cancellationToken"/> 取消時中斷請求（伺服器端同步取消 AI 呼叫），回失敗結果、不丟例外，由呼叫端依自己的取消原因處理。
    /// </summary>
    public async Task<ApiResult<object>> StreamAsync<T>(string url, object? body, Func<T, Task> onItem, CancellationToken cancellationToken)
    {
        try
        {
            var first = await SendStreamOnceAsync(url, body, cancellationToken);
            if (first.StatusCode == HttpStatusCode.Unauthorized)
            {
                first.Dispose();
                if (!await session.RefreshAsync())
                {
                    if (session.IsThrottled)
                    {
                        return ApiResult<object>.FailureResult(ClientSession.ThrottledMessage, (int)HttpStatusCode.TooManyRequests);
                    }

                    await session.SignInAgainAsync();
                    return ApiResult<object>.UnauthorizedResult("登入已失效，請重新登入。");
                }

                first = await SendStreamOnceAsync(url, body, cancellationToken);
            }

            using var response = first;

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            {
                var result = await ReadAsync<object>(response, url);
                Remember(result);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RedirectForForcedSetup(result.ErrorCode);
                }

                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<T>(line, JsonSerializerOptions.Web) is { } item)
                {
                    await onItem(item);
                }
            }

            return ApiResult<object>.SuccessResult(null!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<object>.FailureResult("已取消。", 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JSException)
        {
            // 取消在傳輸層可能先變成 HttpRequestException：呼叫端已經放棄的話，當成取消。
            if (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<object>.FailureResult("已取消。", 0);
            }

            logger.LogWarning(ex, "API stream failed because the server could not be reached. Url={Url}", url);
            return ApiResult<object>.FailureResult(OfflineMessage, 0);
        }
    }

    private async Task<HttpResponseMessage> SendStreamOnceAsync(string url, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (await session.GetAccessTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        // 讓瀏覽器的 fetch 邊收邊交給程式（不等整個回應收齊）。
        request.SetBrowserResponseStreamingEnabled(true);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private ApiResult<T> Parse<T>(Commons.RawResponse raw, string url)
    {
        try
        {
            if (JsonSerializer.Deserialize<ApiResult<T>>(raw.Body, JsonSerializerOptions.Web) is { } result)
            {
                return result;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "API response was not an ApiResult. Url={Url}, Status={Status}", url, raw.Status);
        }

        // IIS 擋下過大的請求時回的是 HTML 錯誤頁（404.13），不是 ApiResult。
        return ApiResult<T>.FailureResult($"伺服器回應異常（HTTP {raw.Status}）。", raw.Status);
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, bool longRunning = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendWithRefreshAsync(method, url, body, longRunning, cancellationToken);
            if (response is null)
            {
                return ApiResult<T>.UnauthorizedResult("登入已失效，請重新登入。");
            }

            var result = await ReadAsync<T>(response, url);
            Remember(result);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                RedirectForForcedSetup(result.ErrorCode);
            }

            return result;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException)
        {
            // 呼叫端已經放棄（取消在傳輸層可能先變成 HttpRequestException）：當成取消，不是連不上伺服器。
            return ApiResult<T>.FailureResult("已取消。", 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JSException)
        {
            // 連不上伺服器、逾時，或刷新 Token（經 JS 的 Web Locks）時出錯：回失敗結果，畫面顯示提示而不是整頁出錯。
            logger.LogWarning(ex, "API request failed because the server could not be reached. Url={Url}", url);
            return ApiResult<T>.FailureResult(OfflineMessage, 0);
        }
    }

    /// <summary>送出請求；拿到 401 時刷新 Token 後重試一次，刷新也失敗就導回登入頁並回 null。</summary>
    private async Task<HttpResponseMessage?> SendWithRefreshAsync(HttpMethod method, string url, object? body, bool longRunning = false, CancellationToken cancellationToken = default)
    {
        var response = await SendOnceAsync(method, url, body, longRunning, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        if (!await session.RefreshAsync())
        {
            // 被限流（0.10.5 起）：暫時的，不叫使用者重新登入，回一個 429 讓呼叫端照一般失敗顯示訊息。
            if (session.IsThrottled)
            {
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = JsonContent.Create(ApiResult<object>.FailureResult(ClientSession.ThrottledMessage, (int)HttpStatusCode.TooManyRequests)),
                };
            }

            await session.SignInAgainAsync();
            return null;
        }

        return await SendOnceAsync(method, url, body, longRunning, cancellationToken);
    }

    private static void Remember<T>(ApiResult<T> result)
    {
        if (!result.Success && !string.IsNullOrEmpty(result.TraceId))
        {
            lastFailure = (result.TraceId, Environment.TickCount64);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string url, object? body, bool longRunning = false, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, url);
        if (await session.GetAccessTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (!longRunning)
        {
            return await http.SendAsync(request, cancellationToken);
        }

        // 同一個網站、同一個瀏覽器的 fetch，只是逾時較長；第一次用到才建立。
        longRunningHttp ??= new HttpClient { BaseAddress = http.BaseAddress, Timeout = LongRunningTimeout };
        foreach (var header in http.DefaultRequestHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return await longRunningHttp.SendAsync(request, cancellationToken);
    }

    private async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, string url)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<ApiResult<T>>() is { } result)
            {
                return result;
            }
        }
        catch (JsonException ex)
        {
            // 不是 ApiResult（例如代理伺服器的錯誤頁）：當成一般失敗。
            logger.LogWarning(ex, "API response was not an ApiResult. Url={Url}, Status={Status}", url, (int)response.StatusCode);
        }

        return ApiResult<T>.FailureResult($"伺服器回應異常（HTTP {(int)response.StatusCode}）。", (int)response.StatusCode);
    }

    private void RedirectForForcedSetup(string? errorCode)
    {
        if (errorCode == ApiErrorCodes.PasswordChangeRequired)
        {
            navigation.NavigateTo("/ChangePassword", forceLoad: true);
        }
        else if (errorCode == ApiErrorCodes.TwoFactorSetupRequired)
        {
            navigation.NavigateTo("/TwoFactorSetup", forceLoad: true);
        }
    }
}

/// <summary><see cref="ApiClient.DownloadAsync"/> 的結果。</summary>
public sealed record ApiDownload(bool Success, string Message, byte[]? Content, string? FileName, string? ContentType, int StatusCode)
{
    public static ApiDownload Failed(string message, int statusCode) => new(false, message, null, null, null, statusCode);
}
