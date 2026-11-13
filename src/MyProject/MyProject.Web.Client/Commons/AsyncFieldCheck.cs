using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// 欄位的非同步檢查（0.10.15 起，.NET 11 的非同步表單驗證）：放在 <c>EditForm</c> 裡，名稱、帳號這類要問伺服器的檢查不必等按「儲存」才知道。
/// <list type="bullet">
/// <item>欄位改變後先清掉舊訊息，等 <see cref="DebounceMilliseconds"/>（避免連續修改時每次都問）再呼叫 <see cref="Check"/>；
/// 回傳的訊息顯示在該欄位的 <c>ValidationMessage</c>。以 <c>EditContext.RegisterAsyncFieldValidator</c> 登記，欄位再變時舊的檢查會被取消。</item>
/// <item>按「儲存」時 <c>EditContext.ValidateAsync()</c> 會等這個檢查完成（<c>ValidationRequestedEventArgs.AddAsyncValidator</c>）。</item>
/// <item>檢查本身失敗（連不上、伺服器錯誤）只記日誌、不顯示訊息、不擋存檔：存檔時伺服器還會再檢查一次。</item>
/// </list>
/// 用法：<c>&lt;AsyncFieldCheck For="() =&gt; CurrentRecord.Name" Check="CheckNameAsync" SaveContext="editContext" /&gt;</c>，<c>CheckNameAsync</c> 回衝突訊息或 null。
/// ⚠️ 在 <c>AntDesign.Form</c> 裡一定要給 <see cref="SaveContext"/>：AntDesign 的 Form 另外建了自己的 <c>EditContext</c>（欄位訊息顯示在它上面），
/// 但頁面按「儲存」時驗證的是外層 <c>EditForm</c> 的（<c>InputWatcher</c> 拿到的那一個）；沒給的話儲存不會等這個檢查（<c>AsyncFieldCheckTests</c> 守門）。
/// </summary>
public sealed class AsyncFieldCheck : ComponentBase, IDisposable
{
    private EditContext? subscribedContext;
    private ValidationMessageStore? store;
    private EditContext? subscribedSaveContext;
    private ValidationMessageStore? saveStore;
    private FieldIdentifier field;

    [CascadingParameter]
    private EditContext? CurrentEditContext { get; set; }

    [Inject]
    private ILogger<AsyncFieldCheck> Logger { get; set; } = default!;

    /// <summary>要檢查的欄位。</summary>
    [Parameter, EditorRequired]
    public Expression<Func<string>> For { get; set; } = default!;

    /// <summary>問伺服器：有問題回要顯示的訊息，沒問題回 null。</summary>
    [Parameter, EditorRequired]
    public Func<Task<string?>> Check { get; set; } = default!;

    /// <summary>
    /// 頁面按「儲存」時驗證的 <c>EditContext</c>（外層 <c>EditForm</c> 的 <c>Context</c>）。與欄位所在的不同時，
    /// 它的 <c>ValidateAsync()</c> 也會等這個檢查，有衝突就驗證失敗。
    /// </summary>
    [Parameter]
    public EditContext? SaveContext { get; set; }

    /// <summary>欄位改變後等多久才問伺服器（毫秒）。</summary>
    [Parameter]
    public int DebounceMilliseconds { get; set; } = 400;

    protected override void OnParametersSet()
    {
        field = FieldIdentifier.Create(For);
        if (!ReferenceEquals(CurrentEditContext, subscribedContext))
        {
            Unsubscribe();
            if (CurrentEditContext is not null)
            {
                subscribedContext = CurrentEditContext;
                store = new ValidationMessageStore(subscribedContext);
                subscribedContext.OnFieldChanged += OnFieldChanged;
                subscribedContext.OnValidationRequested += OnValidationRequested;
            }
        }

        var saveContext = ReferenceEquals(SaveContext, CurrentEditContext) ? null : SaveContext;
        if (!ReferenceEquals(saveContext, subscribedSaveContext))
        {
            UnsubscribeSaveContext();
            if (saveContext is not null)
            {
                subscribedSaveContext = saveContext;
                saveStore = new ValidationMessageStore(saveContext);
                saveContext.OnValidationRequested += OnValidationRequested;
            }
        }
    }

    private void OnFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        if (subscribedContext is null || !e.FieldIdentifier.Equals(field))
        {
            return;
        }

        // 內容變了：舊的「已被使用」立刻拿掉，免得使用者改好了還看到錯誤。
        store?.Clear(field);
        subscribedContext.NotifyValidationStateChanged();
        if (saveStore is not null)
        {
            saveStore.Clear(field);
            subscribedSaveContext?.NotifyValidationStateChanged();
        }

        subscribedContext.RegisterAsyncFieldValidator(field, cancellationToken => RunAsync(debounce: true, forSave: false, cancellationToken));
    }

    private void OnValidationRequested(object? sender, ValidationRequestedEventArgs e)
    {
        try
        {
            // 按儲存（外層）時結果也寫進外層，讓它的驗證失敗、訊息與其他欄位錯誤一起列在上方；打字時只顯示在欄位下方，不重複。
            var forSave = sender is not null && ReferenceEquals(sender, subscribedSaveContext);
            e.AddAsyncValidator(cancellationToken => RunAsync(debounce: false, forSave, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            // 舊的同步 Validate()（已過時）沒有非同步階段：只能交給伺服器存檔時檢查。
            Logger.LogDebug(ex, "Async field check skipped during a synchronous validation. Field={Field}", field.FieldName);
        }
    }

    private async Task RunAsync(bool debounce, bool forSave, CancellationToken cancellationToken)
    {
        if (debounce && DebounceMilliseconds > 0)
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
        }

        string? message;
        try
        {
            message = await Check();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Async field check failed. Field={Field}", field.FieldName);
            message = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        store?.Clear(field);
        saveStore?.Clear(field);
        if (!string.IsNullOrEmpty(message))
        {
            store?.Add(field, message);
            if (forSave)
            {
                saveStore?.Add(field, message);
            }
        }

        subscribedContext?.NotifyValidationStateChanged();
        subscribedSaveContext?.NotifyValidationStateChanged();
    }

    private void Unsubscribe()
    {
        if (subscribedContext is not null)
        {
            subscribedContext.OnFieldChanged -= OnFieldChanged;
            subscribedContext.OnValidationRequested -= OnValidationRequested;
            store?.Clear();
            subscribedContext = null;
            store = null;
        }
    }

    private void UnsubscribeSaveContext()
    {
        if (subscribedSaveContext is not null)
        {
            subscribedSaveContext.OnValidationRequested -= OnValidationRequested;
            saveStore?.Clear();
            subscribedSaveContext = null;
            saveStore = null;
        }
    }

    public void Dispose()
    {
        Unsubscribe();
        UnsubscribeSaveContext();
    }
}
