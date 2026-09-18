using AntDesign;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 表單對話窗的二次確認樣板（離開／儲存）。
///
/// 兩段提示是一組對偶，拆成兩個檔案只會讓日後有人改了一邊忘了另一邊，
/// 文案也必定漂移成「這個窗問得很兇、那個窗問得很客氣」。
/// 沿用 <see cref="TeamBindingConfirm"/> 的既有慣例：static、文案寫死、MaskClosable 一律 false。
///
/// ⚠️ <see cref="AboveFormModal"/> 不可拿掉：新的關窗流程會讓大表單窗在等待確認期間
/// **維持開啟**（見 <see cref="FormModalFlow"/>），確認窗若沿用預設的 1000，
/// 疊放順序就只剩 DOM 先後可以依賴，不夠確定。
/// </summary>
public static class FormEditConfirm
{
    /// <summary>表單對話窗的 z-index 是 1000，確認窗必須疊在它上面。</summary>
    internal const int AboveFormModal = 1100;

    /// <summary>
    /// 有未儲存變更卻要離開時詢問。
    /// 回傳 true 表示使用者選擇「放棄」變更，false 表示要「繼續編輯」。
    /// </summary>
    public static Task<bool> AskDiscardChangesAsync(ModalService modalService)
    {
        return modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "尚有未儲存的變更",
            Content = "離開後將遺失這些變更，確定要放棄嗎？",
            OkText = "放棄變更",
            CancelText = "繼續編輯",
            // 破壞性動作，比照刪除確認加上 Danger。
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
            ZIndex = AboveFormModal,
        });
    }

    /// <summary>
    /// 按下「確定」且確實有變更時詢問。
    /// 回傳 true 表示使用者選擇「儲存」，false 表示要「再檢查」。
    /// </summary>
    public static Task<bool> AskSaveAsync(ModalService modalService)
    {
        return modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認儲存",
            Content = "確定要儲存這筆記錄嗎？",
            OkText = "儲存",
            CancelText = "再檢查",
            // 非破壞性，刻意不加 Danger。
            MaskClosable = false,
            ZIndex = AboveFormModal,
        });
    }
}
