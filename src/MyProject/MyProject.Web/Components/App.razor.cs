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

        /// <summary>網頁登入者的 Id（Cookie 的 Sid；沒登入為 0），傳給 WASM 前端比對瀏覽器存的 Token 是不是這個人的。</summary>
        private int SignedInUserId => int.TryParse(HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Sid)?.Value, out var id) ? id : 0;

        /// <summary>登入頁：載入時清掉瀏覽器存的 Token（見 <c>wwwroot/js/auth-reset.js</c>）。</summary>
        private bool IsLoginPage => HttpContext.Request.Path.StartsWithSegments("/Auths/Login");

        /// <summary>
        /// 每頁的渲染模式：<c>/Auths</c> 靜態伺服器渲染；已搬到 WASM 的頁面 WebAssembly（0.10.1 起）；其餘 Server。
        /// 一律不預先渲染（登入檢查完成前不顯示任何內容）。
        /// </summary>
        public IComponentRenderMode? RenderModeForPage()
        {
            var path = HttpContext.Request.Path.Value ?? "/";
            IComponentRenderMode? renderMode = HttpContext.Request.Path.StartsWithSegments("/Auths")
                ? null
                : IsClientPage
                    ? new InteractiveWebAssemblyRenderMode(prerender: false)
                    : new InteractiveServerRenderMode(prerender: false);
            logger.LogDebug("Resolved render mode for Path={Path}. Mode={Mode}", path, renderMode?.GetType().Name ?? "Static");
            return renderMode;
        }
    }
}
