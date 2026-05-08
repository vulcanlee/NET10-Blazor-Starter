using AntDesign;
using Microsoft.AspNetCore.Localization;
using MyProject.Business.Repositories;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Web.Auth;
using MyProject.Web.Components.Layout;
using MyProject.Web.Localization;
using System.Globalization;

namespace MyProject.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredLocalization(this IServiceCollection services)
    {
        services.AddLocalization();

        var supportedCultures = new[]
        {
            new CultureInfo("zh-TW"),
            new CultureInfo("en-US")
        };

        var defaultCulture = supportedCultures[0];

        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.DefaultRequestCulture = new RequestCulture(defaultCulture);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;

            options.RequestCultureProviders = new List<IRequestCultureProvider>
            {
                new AcceptLanguageHeaderRequestCultureProvider()
            };
        });

        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = defaultCulture.Name;

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<AuthenticationStateHelper>();
        services.AddScoped<CurrentUserService>();
        services.AddScoped<MyUserServiceLogin>();
        services.AddScoped<SidebarMenuService>();
        services.AddScoped<RolePermissionService>();
        services.AddScoped<RoleViewService>();
        services.AddScoped<MyUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectRepository>();
        services.AddScoped<MyTaskRepository>();
        services.AddScoped<MeetingRepository>();
        services.AddScoped<MyTasService>();
        services.AddScoped<MeetingService>();

        return services;
    }
}
