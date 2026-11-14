using System.Net;
using Microsoft.AspNetCore.Http;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// 稽核紀錄的來源 IP（0.9.117 起）：Web API 從 HttpContext 取，排程作業沒有 HttpContext。
/// </summary>
public sealed class ClientIpProviderTests
{
    [Fact]
    public void Format_ShouldMapIpv4MappedIpv6BackToIpv4()
        => Assert.Equal("192.0.2.1", ClientIpProvider.Format(IPAddress.Parse("::ffff:192.0.2.1")));

    [Fact]
    public void Format_ShouldKeepPlainIpv6()
        => Assert.Equal("2001:db8::1", ClientIpProvider.Format(IPAddress.Parse("2001:db8::1")));

    [Fact]
    public void Format_Null_ShouldReturnNull()
        => Assert.Null(ClientIpProvider.Format(null));

    [Fact]
    public void GetClientIp_ShouldReadHttpContextRemoteIp()
    {
        var provider = new ClientIpProvider(CreateAccessor("203.0.113.5"));

        Assert.Equal("203.0.113.5", provider.GetClientIp());
    }

    /// <summary>排程作業：沒有 HttpContext。</summary>
    [Fact]
    public void GetClientIp_WithoutHttpContext_ShouldReturnNull()
    {
        var provider = new ClientIpProvider(new HttpContextAccessor());

        Assert.Null(provider.GetClientIp());
    }

    private static HttpContextAccessor CreateAccessor(string ip)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return new HttpContextAccessor { HttpContext = context };
    }
}
