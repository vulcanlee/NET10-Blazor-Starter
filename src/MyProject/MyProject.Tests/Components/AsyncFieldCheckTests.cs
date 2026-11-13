using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using MyProject.Web.Client.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 欄位的非同步檢查（0.10.15 起，.NET 11 的非同步表單驗證）：名稱、帳號是否已被使用，不必等按「儲存」才知道。
/// 欄位改變後（等一下，避免每打一個字就問一次）呼叫伺服器檢查，有衝突就在欄位下方顯示；按「儲存」時 <c>ValidateAsync</c> 會等這個檢查完成。
/// </summary>
public sealed class AsyncFieldCheckTests : ComponentTestBase
{
    private sealed class Model
    {
        public string Name { get; set; } = string.Empty;
    }

    private (IRenderedComponent<IComponent> Cut, EditContext Context, Model Model) RenderForm(Func<string, Task<string?>> check)
    {
        var model = new Model();
        var context = new EditContext(model);
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<CascadingValue<EditContext>>(0);
            builder.AddAttribute(1, "Value", context);
            builder.AddAttribute(2, "IsFixed", true);
            builder.AddAttribute(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<AsyncFieldCheck>(0);
                inner.AddAttribute(1, nameof(AsyncFieldCheck.For), (System.Linq.Expressions.Expression<Func<string>>)(() => model.Name));
                inner.AddAttribute(2, nameof(AsyncFieldCheck.Check), (Func<Task<string?>>)(() => check(model.Name)));
                inner.AddAttribute(3, nameof(AsyncFieldCheck.DebounceMilliseconds), 10);
                inner.CloseComponent();
                inner.OpenComponent<ValidationMessage<string>>(4);
                inner.AddAttribute(5, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => model.Name));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        var cut = Render(fragment);
        return ((IRenderedComponent<IComponent>)cut, context, model);
    }

    [Fact]
    public async Task ChangingTheField_ShouldShowTheServersMessage_UnderTheField()
    {
        var (cut, context, model) = RenderForm(name => Task.FromResult<string?>(name == "重複" ? "分類名稱已存在，無法新增。" : null));

        model.Name = "重複";
        await cut.InvokeAsync(() => context.NotifyFieldChanged(FieldIdentifier.Create(() => model.Name)));

        cut.WaitForAssertion(() => Assert.Contains("分類名稱已存在", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        model.Name = "沒問題";
        await cut.InvokeAsync(() => context.NotifyFieldChanged(FieldIdentifier.Create(() => model.Name)));

        cut.WaitForAssertion(() => Assert.DoesNotContain("分類名稱已存在", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ValidateAsync_ShouldAwaitTheCheck_AndFailWhenTheNameIsTaken()
    {
        var (cut, context, model) = RenderForm(async name =>
        {
            await Task.Delay(50);
            return name == "重複" ? "分類名稱已存在，無法新增。" : null;
        });

        model.Name = "重複";
        var valid = await cut.InvokeAsync(() => context.ValidateAsync());

        Assert.False(valid);
        Assert.Contains("分類名稱已存在，無法新增。", context.GetValidationMessages());

        model.Name = "沒問題";
        Assert.True(await cut.InvokeAsync(() => context.ValidateAsync()));
    }

    [Fact]
    public async Task InsideAnAntDesignForm_SavingWithTheOuterEditContext_ShouldAwaitTheCheck()
    {
        // 實際頁面的結構：EditForm（按儲存時驗證它的 EditContext，InputWatcher 拿到的那一個）→ AntDesign.Form（另外建了自己的 EditContext）→ 欄位。
        // 沒有 SaveContext 時，外層的 ValidateAsync 不會等這個檢查（獨立審查發現）。
        var model = new Model { Name = "重複" };
        EditContext? outer = null;
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddAttribute(1, nameof(EditForm.Model), model);
            builder.AddAttribute(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(outerContext => inner =>
            {
                outer = outerContext;
                inner.OpenComponent<AntDesign.Form<Model>>(0);
                inner.AddAttribute(1, "Model", model);
                inner.AddAttribute(2, "ChildContent", (RenderFragment<Model>)(_ => field =>
                {
                    field.OpenComponent<AsyncFieldCheck>(0);
                    field.AddAttribute(1, nameof(AsyncFieldCheck.For), (System.Linq.Expressions.Expression<Func<string>>)(() => model.Name));
                    field.AddAttribute(2, nameof(AsyncFieldCheck.Check), (Func<Task<string?>>)(() => Task.FromResult<string?>(model.Name == "重複" ? "分類名稱已存在，無法新增。" : null)));
                    field.AddAttribute(3, nameof(AsyncFieldCheck.SaveContext), outerContext);
                    field.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        var cut = Render(fragment);

        Assert.NotNull(outer);
        Assert.False(await cut.InvokeAsync(() => outer!.ValidateAsync()));
        Assert.Contains("分類名稱已存在，無法新增。", outer!.GetValidationMessages());

        model.Name = "沒問題";
        Assert.True(await cut.InvokeAsync(() => outer!.ValidateAsync()));
    }

    [Fact]
    public void EveryUsageInsideAPage_ShouldPassTheSaveContext()
    {
        // 頁面的表單都是 EditForm 包 AntDesign.Form：沒給 SaveContext，按儲存不會等檢查（見上一個測試）。含 CRUD 產生器樣板。
        var files = SourceTree.UiFiles("*.razor")
            .Append(Path.Combine(SourceTree.Scripts, "crud-templates", "Web.Client", "Views", "__Plural__", "__Name__ViewView.razor.tmpl"));
        var usages = files
            .SelectMany(path => File.ReadAllLines(path).Where(line => line.Contains("<AsyncFieldCheck ", StringComparison.Ordinal)).Select(line => (path, line)))
            .ToList();

        Assert.NotEmpty(usages);
        var missing = usages.Where(x => !x.line.Contains("SaveContext=", StringComparison.Ordinal)).Select(x => Path.GetFileName(x.path)).ToList();
        Assert.True(missing.Count == 0, "這些 AsyncFieldCheck 沒有給 SaveContext（外層 EditForm 的 Context）：" + string.Join("、", missing));
    }

    [Fact]
    public async Task ACheckThatFails_ShouldNotBlockSaving()
    {
        // 檢查本身失敗（連不上、伺服器錯誤）不擋存檔：存檔時伺服器還會再檢查一次。
        var (cut, context, model) = RenderForm(_ => throw new HttpRequestException("連不上"));

        model.Name = "任何名稱";

        Assert.True(await cut.InvokeAsync(() => context.ValidateAsync()));
    }
}
