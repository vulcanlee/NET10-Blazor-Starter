using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

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

        /// <summary>這次整頁請求的路徑是不是已搬到 WASM 的頁面（0.10.1 起）。</summary>
        private bool IsClientPage => MyProject.Web.Client.ClientRouteTable.IsClientRoute(HttpContext.Request.Path.Value);

        /// <summary>
        /// 每頁的渲染模式：已搬到 WASM 的頁面 WebAssembly（0.10.1 起；0.10.8 起登入相關頁面也是，已沒有靜態伺服器渲染的頁面）；其餘 Server。
        /// 一律不預先渲染（登入檢查完成前不顯示任何內容）。
        /// </summary>
        public IComponentRenderMode? RenderModeForPage()
        {
            var path = HttpContext.Request.Path.Value ?? "/";
            IComponentRenderMode? renderMode = IsClientPage
                ? new InteractiveWebAssemblyRenderMode(prerender: false)
                : new InteractiveServerRenderMode(prerender: false);
            logger.LogDebug("Resolved render mode for Path={Path}. Mode={Mode}", path, renderMode?.GetType().Name ?? "Static");
            return renderMode;
        }
    }
}
