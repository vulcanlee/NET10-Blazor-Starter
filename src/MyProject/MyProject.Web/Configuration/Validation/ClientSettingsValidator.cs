using Microsoft.Extensions.Options;

namespace MyProject.Web.Configuration.Validation;

/// <summary>
/// <c>ClientSettings:BaseUrl</c>（0.10.10 起）：有填時必須是完整的 http(s) 網址、不帶 query 或錨點 ——
/// 它會被接上頁面路徑當成導回網址與信中連結，也會被當成 CORS 的允許來源。正式環境必須是 https 另由 <see cref="StartupSafetyValidator"/> 檢查。
/// </summary>
public sealed class ClientSettingsValidator : IValidateOptions<ClientSettings>
{
    public ValidateOptionsResult Validate(string? name, ClientSettings options)
    {
        var errors = new OptionsErrors();
        if (options.IsSeparateSite
            && (!OptionsErrors.IsHttpUrl(options.BaseUrl.Trim())
                || !Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var uri)
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0))
        {
            errors.Add($"{ClientSettings.SectionName}:BaseUrl",
                $"必須是以 http:// 或 https:// 開頭的完整網址、不帶 ? 或 #（例如 https://app.example.com/），目前是「{options.BaseUrl}」；同站台部署請留空。");
        }

        return errors.ToResult();
    }
}
