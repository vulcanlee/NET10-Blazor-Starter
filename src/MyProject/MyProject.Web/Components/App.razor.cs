using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using NLog;

namespace MyProject.Web.Components
{
    public partial class App
    {
        private readonly ILogger<App> logger;

        public App(ILogger<App> logger)
        {
            this.logger = logger;
        }

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        public IComponentRenderMode? RenderModeForPage()
        {
            var foo = HttpContext.Request.Path.StartsWithSegments("/Auths")
          ? null : new InteractiveServerRenderMode(prerender: false);

            logger.LogInformation($"使用渲染模式 : RenderModeForPage: {foo}");
            return foo;
        }
    }
}
