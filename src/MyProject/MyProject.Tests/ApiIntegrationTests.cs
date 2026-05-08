using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Web;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyProject.Tests;

public sealed class ApiIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApiTestApplicationFactory factory;

    public ApiIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutBearerToken_ShouldReturnApiResult401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task AuthEndpoints_LoginRefreshAndMe_ShouldReturnApiResult()
    {
        using var client = factory.CreateClient();

        var loginResult = await LoginAsync(client);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.RefreshToken));

        var refreshResponse = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto
        {
            RefreshToken = loginResult.Data!.RefreshToken
        });
        var refreshResult = await ReadApiResultAsync<TokenResponseDto>(refreshResponse);

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        Assert.True(refreshResult.Success);
        Assert.False(string.IsNullOrWhiteSpace(refreshResult.Data?.AccessToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data.AccessToken);
        var meResponse = await client.GetAsync("/api/Auth/me");
        var meResult = await ReadApiResultAsync<CurrentUserDto>(meResponse);

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.True(meResult.Success);
        Assert.Equal("support", meResult.Data?.Account);
    }

    [Fact]
    public async Task ProjectCreate_InvalidPayload_ShouldReturnApiResult400()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.PostAsJsonAsync("/api/Project", new { });
        var result = await ReadApiResultAsync<ProjectDto>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(result.Errors);
    }

    [Fact]
    public async Task ProjectCrud_WithBearerToken_ShouldUseApiResultAndDto()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var createDto = new ProjectCreateUpdateDto
        {
            Id = 1,
            Title = $"Integration Project {Guid.NewGuid():N}",
            Description = "Integration test project",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Priority = "中",
            CompletionPercentage = 10,
            Owner = "integration-test"
        };

        var createResponse = await client.PostAsJsonAsync("/api/Project", createDto);
        var createResult = await ReadApiResultAsync<ProjectDto>(createResponse);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.True(createResult.Success);
        Assert.NotNull(createResult.Data);
        Assert.True(createResult.Data!.Id > 0);
        Assert.Equal(createDto.Title, createResult.Data.Title);

        var getResponse = await client.GetAsync($"/api/Project/{createResult.Data.Id}");
        var getResult = await ReadApiResultAsync<ProjectDto>(getResponse);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResult.Success);
        Assert.Equal(createDto.Title, getResult.Data?.Title);
    }

    private static async Task AuthorizeAsync(HttpClient client)
    {
        var loginResult = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
    }

    private static async Task<ApiResult<TokenResponseDto>> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });

        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result;
    }

    private static async Task<ApiResult<T>> ReadApiResultAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<T>>(json, JsonOptions);
        Assert.NotNull(result);
        return result!;
    }
}

public sealed class ApiTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "MyProjectIntegrationTests",
        Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string> environmentVariables;

    public ApiTestApplicationFactory()
    {
        environmentVariables = CreateSettings()
            .ToDictionary(
                x => x.Key.Replace(":", "__", StringComparison.Ordinal),
                x => x.Value ?? string.Empty);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(CreateSettings());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, null);
        }

        if (disposing && Directory.Exists(rootPath))
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may release file handles shortly after the test host stops.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup keeps integration test assertions independent from OS file timing.
            }
        }
    }

    private Dictionary<string, string?> CreateSettings()
    {
        return new Dictionary<string, string?>
        {
            ["NLog:BasePath"] = Path.Combine(rootPath, "Logs"),
            ["JwtSettings:Issuer"] = "MyProject.Tests",
            ["JwtSettings:Audience"] = "MyProject.Tests.Api",
            ["JwtSettings:SigningKey"] = "IntegrationTests-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["JwtSettings:AccessTokenMinutes"] = "30",
            ["JwtSettings:RefreshTokenDays"] = "7",
            ["JwtSettings:ClockSkewMinutes"] = "0",
            ["SystemSettings:ExternalFileSystem:DatabasePath"] = Path.Combine(rootPath, "DB"),
            ["SystemSettings:ExternalFileSystem:DownloadPath"] = Path.Combine(rootPath, "Download"),
            ["SystemSettings:ExternalFileSystem:UploadPath"] = Path.Combine(rootPath, "Upload"),
            ["SystemSettings:ExternalFileSystem:ProjectFilePath"] = Path.Combine(rootPath, "ProjectFile"),
            ["SystemSettings:ExternalFileSystem:TaskFilePath"] = Path.Combine(rootPath, "TaskFile"),
            ["SystemSettings:ExternalFileSystem:MeetingFilePath"] = Path.Combine(rootPath, "MeetingFile")
        };
    }
}
