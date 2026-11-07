using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 把信交給背景寄送，立即返回。
///
/// <para>為什麼不直接呼叫 <see cref="IEmailSender"/>：匿名流程（忘記密碼）若同步寄信，
/// 「帳號存在」會比「帳號不存在」慢上數秒，回應時間本身就洩漏了帳號是否存在。</para>
///
/// <para>0.9.167 起「先存再寄」：這裡只把信放進記憶體交接區就返回（不碰資料庫 —— 例外告警信是在例外記錄管線的
/// 鎖定區段內入列的）；背景寄信器再把它寫進 <c>EmailOutbox</c>（內文加密）並寄出，暫時性失敗會重試，程式重新啟動後接著寄。
/// 只有程式崩潰時，交接區裡還沒存進資料庫的那幾毫秒的信會遺失。</para>
/// </summary>
public interface IEmailQueue
{
    /// <summary>成功入列回傳 true；佇列已滿回傳 false（信不會寄出）。</summary>
    bool TryEnqueue(EmailMessage message);
}
