using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyProject.Web.Client.Auth;

/// <summary>兩步驟驗證頁要用的待驗登入（登入或 Google 兌換之後存在 sessionStorage，只給這個分頁）。</summary>
public sealed record PendingTwoFactor(string Ticket, int RememberDeviceDays, string ReturnUrl);

/// <summary>
/// 登入流程存在瀏覽器的暫存（0.10.8 起，WASM 遷移 M9；取代 Server 版的待驗 Cookie 與記住裝置 Cookie）：
/// <list type="bullet">
/// <item>待驗登入（sessionStorage，5 分鐘票證，關掉分頁就沒了）。</item>
/// <item>記住裝置 Token（localStorage，伺服器加密、綁定使用者與工作階段版本，換版本即失效）。</item>
/// <item>Google 登入的瀏覽器驗證值（sessionStorage；只把雜湊帶給伺服器，兌換代碼時出示原值）。</item>
/// </list>
/// </summary>
public static class LoginStorage
{
    internal const string PendingKey = "myproject.tfa-pending";
    internal const string DeviceKey = "myproject.tfa-device";
    internal const string GoogleVerifierKey = "myproject.google-verifier";

    public static async Task<PendingTwoFactor?> ReadPendingAsync(TokenStore store)
    {
        var json = await store.GetItemAsync(PendingKey, persistent: false);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingTwoFactor>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task WritePendingAsync(TokenStore store, PendingTwoFactor pending)
        => store.SetItemAsync(PendingKey, JsonSerializer.Serialize(pending), persistent: false);

    public static Task ClearPendingAsync(TokenStore store) => store.RemoveItemAsync(PendingKey, persistent: false);

    public static Task<string?> ReadDeviceTokenAsync(TokenStore store) => store.GetItemAsync(DeviceKey, persistent: true);

    public static Task WriteDeviceTokenAsync(TokenStore store, string token) => store.SetItemAsync(DeviceKey, token, persistent: true);

    /// <summary>產生這次 Google 登入的驗證值（存在這個分頁），回傳要帶給伺服器的雜湊（SHA-256 → Base64Url，與伺服器相同）。</summary>
    public static async Task<string> NewGoogleBindingAsync(TokenStore store)
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await store.SetItemAsync(GoogleVerifierKey, verifier, persistent: false);
        return HashVerifier(verifier);
    }

    public static Task<string?> ReadGoogleVerifierAsync(TokenStore store) => store.GetItemAsync(GoogleVerifierKey, persistent: false);

    public static string HashVerifier(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
}
