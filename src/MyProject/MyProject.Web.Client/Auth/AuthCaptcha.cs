namespace MyProject.Web.Components.Auths;

/// <summary>
/// 登入頁與忘記密碼頁共用的 4 碼數字驗證碼（0.10.8 起在 WASM 產生，命名空間不變）。
///
/// ⚠️ 答案就在頁面上（隱藏欄位 <c>Input.CaptchaCode</c>，0.10.8 起在瀏覽器產生與比對），讀得到頁面的腳本就讀得到答案 ——
/// 它只擋「隨手連點」，**不是防機器人的機制**。真正的防線是伺服器的登入配額（限流）與帳號鎖定、忘記密碼的同帳號冷卻時間。
/// </summary>
public static class AuthCaptcha
{
    public const int Length = 4;

    public static string Generate()
    {
        return Random.Shared.Next(0, (int)Math.Pow(10, Length)).ToString($"D{Length}");
    }
}
