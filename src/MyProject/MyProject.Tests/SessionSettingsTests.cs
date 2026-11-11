using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// `SessionSettings`（0.10.8 起取代 `CookieSettings`）以 ValidateDataAnnotations + ValidateOnStart 註冊，
/// 設定寫壞時是**啟動就失敗**而不是執行期才出事。這裡釘住邊界與舊鍵相容。
/// </summary>
public sealed class SessionSettingsTests
{
    private static IReadOnlyList<ValidationResult> Validate(SessionSettings settings)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results;
    }

    /// <summary>
    /// 預設值必須是有效的 —— 出貨的 appsettings.json 若一開始就驗不過，服務起不來。
    /// </summary>
    [Fact]
    public void Defaults_ShouldBeValid()
    {
        var settings = new SessionSettings();

        Assert.Empty(Validate(settings));
        Assert.Equal(480, settings.IdleTimeoutMinutes);
        Assert.Equal(30, settings.RememberMeDays);
        Assert.Equal(15, settings.AccessTokenMinutes);
        Assert.Equal(5, settings.ValidationIntervalMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(43201)]
    public void IdleTimeoutMinutes_OutOfRange_ShouldFailValidation(int minutes)
    {
        var settings = new SessionSettings { IdleTimeoutMinutes = minutes };

        Assert.Contains(Validate(settings), x => x.MemberNames.Contains(nameof(SessionSettings.IdleTimeoutMinutes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void RememberMeDays_OutOfRange_ShouldFailValidation(int days)
    {
        var settings = new SessionSettings { RememberMeDays = days };

        Assert.Contains(Validate(settings), x => x.MemberNames.Contains(nameof(SessionSettings.RememberMeDays)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void AccessTokenMinutes_OutOfRange_ShouldFailValidation(int minutes)
    {
        var settings = new SessionSettings { AccessTokenMinutes = minutes };

        Assert.Contains(Validate(settings), x => x.MemberNames.Contains(nameof(SessionSettings.AccessTokenMinutes)));
    }

    /// <summary>
    /// 「記住我」的效期必須比一般登入長，否則勾了反而更短，語意就反了。
    /// 這條釘的是**出貨預設值之間的關係**，不是使用者改完之後的值。
    /// </summary>
    [Fact]
    public void RememberMe_ShouldOutliveNormalLogin_ByDefault()
    {
        var settings = new SessionSettings();

        Assert.True(TimeSpan.FromDays(settings.RememberMeDays) > TimeSpan.FromMinutes(settings.IdleTimeoutMinutes));
        Assert.True(TimeSpan.FromMinutes(settings.AccessTokenMinutes) < TimeSpan.FromMinutes(settings.IdleTimeoutMinutes));
    }

    /// <summary>
    /// 舊鍵相容（0.10.7 以前叫 CookieSettings）：部署者在環境變數或 appsettings.Production.json 設過的值，升級後要照樣生效，
    /// 不可悄悄變回預設值；回傳用到的舊鍵讓啟動時記警告。
    /// </summary>
    /// <summary>舊的 CookieSettings:SlidingExpiration=false（8 小時絕對上限）要對應到 IdleTimeoutSliding=false，不可悄悄變成永不到期（0.10.8 獨立審查）。</summary>
    [Fact]
    public void LegacySlidingExpirationFalse_ShouldTurnOffSliding_AndBeReported()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CookieSettings:SlidingExpiration"] = "false",
        }).Build();

        var settings = SessionSettings.Read(configuration);

        Assert.False(settings.IdleTimeoutSliding);
        Assert.Contains("CookieSettings:SlidingExpiration", SessionSettings.ApplyLegacyCookieSettings(new SessionSettings(), configuration));
        Assert.True(SessionSettings.Read(new ConfigurationBuilder().Build()).IdleTimeoutSliding);
    }

    [Fact]
    public void LegacyCookieSettings_ShouldStillApply_AndBeReported()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SessionSettings:IdleTimeoutMinutes"] = "480",
            ["SessionSettings:RememberMeDays"] = "30",
            ["CookieSettings:ExpireMinutes"] = "60",
            ["CookieSettings:RememberMeDays"] = "7",
            ["CookieSettings:ValidationIntervalMinutes"] = "2",
        }).Build();

        var settings = SessionSettings.Read(configuration);

        Assert.Equal(60, settings.IdleTimeoutMinutes);
        Assert.Equal(7, settings.RememberMeDays);
        Assert.Equal(2, settings.ValidationIntervalMinutes);
        Assert.Equal(
            ["CookieSettings:ExpireMinutes", "CookieSettings:RememberMeDays", "CookieSettings:ValidationIntervalMinutes"],
            SessionSettings.ApplyLegacyCookieSettings(new SessionSettings(), configuration));
    }

    [Fact]
    public void WithoutLegacyKeys_ShouldUseTheNewSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SessionSettings:IdleTimeoutMinutes"] = "120",
            ["SessionSettings:AccessTokenMinutes"] = "10",
        }).Build();

        var settings = SessionSettings.Read(configuration);

        Assert.Equal(120, settings.IdleTimeoutMinutes);
        Assert.Equal(10, settings.AccessTokenMinutes);
        Assert.Empty(SessionSettings.ApplyLegacyCookieSettings(new SessionSettings(), configuration));
    }
}
