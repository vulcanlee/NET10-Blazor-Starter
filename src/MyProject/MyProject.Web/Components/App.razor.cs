using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MyProject.Web.Components
{
    public partial class App
    {
        /// <summary>
        /// 每頁的渲染模式：一律以 WebAssembly 執行（0.10.9 起已沒有 Server 互動模式），不預先渲染（登入檢查完成前不顯示任何內容）。
        /// </summary>
        private static readonly IComponentRenderMode PageRenderMode = new InteractiveWebAssemblyRenderMode(prerender: false);
    }
}
