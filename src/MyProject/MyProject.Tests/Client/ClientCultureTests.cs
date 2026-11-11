using System.Globalization;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Commons;

namespace MyProject.Tests.Client;

/// <summary>WASM 頁面的日期時間格式與伺服器相同（0.10.2 起，WASM 遷移 M3）。</summary>
public sealed class ClientCultureTests
{
    [Fact]
    public void Build_WithTheServerFormat_ShouldFormatDatesExactlyLikeTheServer()
    {
        var server = new CultureInfo("zh-TW");
        var format = new DateTimeFormatDto
        {
            ShortDatePattern = server.DateTimeFormat.ShortDatePattern,
            LongDatePattern = server.DateTimeFormat.LongDatePattern,
            ShortTimePattern = server.DateTimeFormat.ShortTimePattern,
            LongTimePattern = server.DateTimeFormat.LongTimePattern,
            AMDesignator = server.DateTimeFormat.AMDesignator,
            PMDesignator = server.DateTimeFormat.PMDesignator,
        };
        var morning = new DateTime(2026, 11, 1, 9, 0, 0);
        var evening = new DateTime(2026, 11, 1, 21, 5, 7);

        var client = ClientCulture.Build(format);

        Assert.Equal(ClientCulture.Name, client.Name);
        Assert.Equal(morning.ToString(server), morning.ToString(client));
        Assert.Equal(evening.ToString(server), evening.ToString(client));
    }

    [Fact]
    public void Build_WithACustomTimePattern_ShouldUseIt()
    {
        var client = ClientCulture.Build(new DateTimeFormatDto { ShortDatePattern = "yyyy/M/d", LongTimePattern = "tt hh:mm:ss", AMDesignator = "上午", PMDesignator = "下午" });

        Assert.Equal("2026/11/1 上午 09:00:00", new DateTime(2026, 11, 1, 9, 0, 0).ToString(client));
    }

    /// <summary>WASM 的表格欄位用明確的格式（瀏覽器渲染時的語系換不掉），結果要等於伺服器的 <c>DateTime.ToString()</c>。</summary>
    [Fact]
    public void DisplayFormat_ShouldRenderLikeTheServerToString()
    {
        var server = new CultureInfo("zh-TW");
        var client = ClientCulture.Build(new DateTimeFormatDto
        {
            ShortDatePattern = server.DateTimeFormat.ShortDatePattern,
            LongTimePattern = server.DateTimeFormat.LongTimePattern,
        });
        var value = new DateTime(2026, 11, 1, 21, 5, 7);

        Assert.Equal(value.ToString(server), value.ToString(ClientCulture.DisplayFormatOf(client), server));
    }

    [Fact]
    public void Build_WithoutAFormat_ShouldStillBeTraditionalChinese()
        => Assert.Equal("zh-TW", ClientCulture.Build(null).Name);

    /// <summary>審查修正（0.10.4）：日期選擇器的值去掉時區標記再送，序列化後不帶瀏覽器的時差，伺服器照自己的時區解讀。</summary>
    [Fact]
    public void Unzoned_ShouldDropTheKind_SoNoOffsetIsSerialized()
    {
        var picked = new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Local);

        var sent = ClientCulture.Unzoned(picked)!.Value;

        Assert.Equal(DateTimeKind.Unspecified, sent.Kind);
        Assert.Equal("\"2026-11-01T09:30:00\"", System.Text.Json.JsonSerializer.Serialize(sent));
        Assert.Null(ClientCulture.Unzoned(null));
    }
}
