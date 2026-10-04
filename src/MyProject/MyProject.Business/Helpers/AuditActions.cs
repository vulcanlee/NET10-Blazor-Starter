namespace MyProject.Business.Helpers;

/// <summary>
/// 稽核動作代碼的唯一來源（0.9.78，LOG-14）。
///
/// 0.9.77 之前約 30 個代碼散在各呼叫點的字串字面值裡，打錯字只會在查詢頁多出一個孤兒代碼，沒有任何東西擋得下。
/// 現在寫稽核一律用這裡的常數，由 <c>AuditConventionTests</c> 守門（呼叫點不得出現字串字面值）。
///
/// 格式為「資源.動作」；第一段是稽核紀錄頁的分類與標籤顏色（<c>AuditLogAdapterModel.ActionCategory</c>）。
/// 值會寫進資料庫，<b>既有代碼的字串不可更改</b>，否則舊紀錄與新紀錄會被當成兩種動作。
/// </summary>
public static class AuditActions
{
    public static class Login
    {
        public const string Success = "Login.Success";
        public const string Failed = "Login.Failed";
        public const string Disabled = "Login.Disabled";
        public const string LockedOut = "Login.LockedOut";
        public const string SsoSuccess = "Login.Sso.Success";
        public const string SsoFailed = "Login.Sso.Failed";
        /// <summary>工作階段已失效（改密碼、停用、角色變更、強制登出後的舊登入）被系統登出（0.9.103 起）。</summary>
        public const string SessionExpired = "Login.SessionExpired";
        /// <summary>登入第二步的驗證碼或備用碼錯誤（0.9.104 起，計入登入失敗次數）。</summary>
        public const string TwoFactorFailed = "Login.TwoFactorFailed";
    }

    public const string Logout = "Logout";

    public static class User
    {
        public const string Create = "User.Create";

        /// <summary>使用者管理匯出 Excel（0.9.107 起）。</summary>
        public const string Export = "User.Export";
        public const string Update = "User.Update";
        /// <summary>軟刪除（0.9.95 起）；還原與永久刪除見 <see cref="Restore"/>、<see cref="Purge"/>。</summary>
        public const string Delete = "User.Delete";
        public const string Restore = "User.Restore";
        public const string Purge = "User.Purge";
        /// <summary>排程作業「已刪除資料清理」依保留天數自動永久刪除（0.9.97 起；每次一筆彙總）。</summary>
        public const string AutoPurge = "User.AutoPurge";
        public const string SsoCreate = "User.SsoCreate";
        public const string SsoLink = "User.SsoLink";
        /// <summary>管理員在使用者清單解除登入鎖定（0.9.101 起）。</summary>
        public const string Unlock = "User.Unlock";
        /// <summary>使用者在個人資料頁修改自己的姓名（0.9.102 起）。</summary>
        public const string ProfileUpdate = "User.ProfileUpdate";
        /// <summary>管理員強制登出這位使用者所有的工作階段（0.9.103 起）。</summary>
        public const string ForceLogout = "User.ForceLogout";
        /// <summary>兩步驟驗證：本人啟用、停用、重新產生備用碼；管理員重設（0.9.104 起）。</summary>
        public const string TwoFactorEnable = "User.TwoFactorEnable";
        public const string TwoFactorDisable = "User.TwoFactorDisable";
        public const string TwoFactorBackupCodesRegenerate = "User.TwoFactorBackupCodesRegenerate";
        public const string TwoFactorReset = "User.TwoFactorReset";
    }

    public static class Role
    {
        public const string Create = "Role.Create";
        public const string Update = "Role.Update";
        /// <summary>軟刪除（0.9.95 起）；還原與永久刪除見 <see cref="Restore"/>、<see cref="Purge"/>。</summary>
        public const string Delete = "Role.Delete";
        public const string Restore = "Role.Restore";
        public const string Purge = "Role.Purge";
        /// <summary>排程作業「已刪除資料清理」依保留天數自動永久刪除（0.9.97 起；每次一筆彙總）。</summary>
        public const string AutoPurge = "Role.AutoPurge";
    }

    public static class Permission
    {
        public const string Denied = "Permission.Denied";
    }

    public static class Password
    {
        public const string ResetRequested = "Password.ResetRequested";
        public const string ResetCompleted = "Password.ResetCompleted";
        public const string ResetFailed = "Password.ResetFailed";
        public const string Changed = "Password.Changed";
    }

    public static class Token
    {
        public const string RefreshFailed = "Token.RefreshFailed";
    }

    public static class Project
    {
        public const string Create = "Project.Create";

        /// <summary>專案項目匯出 Excel（0.9.107 起）。</summary>
        public const string Export = "Project.Export";
        public const string Update = "Project.Update";
        /// <summary>0.9.94 起為軟刪除（可還原）。</summary>
        public const string Delete = "Project.Delete";
        public const string Restore = "Project.Restore";
        /// <summary>永久刪除（只能對已刪除的資料執行，無法復原）。</summary>
        public const string Purge = "Project.Purge";
        /// <summary>排程作業「已刪除資料清理」依保留天數自動永久刪除（0.9.97 起；每次一筆彙總）。</summary>
        public const string AutoPurge = "Project.AutoPurge";
        public const string FileUpload = "Project.FileUpload";
        public const string FileDelete = "Project.FileDelete";
        public const string FileDownload = "Project.FileDownload";
    }

    public static class Category
    {
        public const string Create = "Category.Create";

        /// <summary>分類清單匯出 Excel（0.9.107 起）。</summary>
        public const string Export = "Category.Export";
        public const string Update = "Category.Update";
        /// <summary>0.9.94 起為軟刪除（可還原）。</summary>
        public const string Delete = "Category.Delete";
        public const string Restore = "Category.Restore";
        /// <summary>永久刪除（只能對已刪除的資料執行，無法復原）。</summary>
        public const string Purge = "Category.Purge";
        /// <summary>排程作業「已刪除資料清理」依保留天數自動永久刪除（0.9.97 起；每次一筆彙總）。</summary>
        public const string AutoPurge = "Category.AutoPurge";
    }

    public static class Team
    {
        public const string Create = "Team.Create";

        /// <summary>團隊清單匯出 Excel（0.9.107 起）。</summary>
        public const string Export = "Team.Export";
        public const string Update = "Team.Update";
        /// <summary>0.9.94 起為軟刪除（可還原）。</summary>
        public const string Delete = "Team.Delete";
        public const string Restore = "Team.Restore";
        /// <summary>永久刪除（只能對已刪除的資料執行，無法復原）。</summary>
        public const string Purge = "Team.Purge";
        /// <summary>排程作業「已刪除資料清理」依保留天數自動永久刪除（0.9.97 起；每次一筆彙總）。</summary>
        public const string AutoPurge = "Team.AutoPurge";
    }

    public static class LogLevel
    {
        public const string Apply = "LogLevel.Apply";
        public const string Restore = "LogLevel.Restore";
    }

    public static class LogViewer
    {
        public const string AiAnalyze = "LogViewer.AiAnalyze";
        public const string AiAnalyzeExportPdf = "LogViewer.AiAnalyzeExportPdf";
        public const string Export = "LogViewer.Export";
    }

    public static class ExceptionLog
    {
        public const string AiAnalyze = "ExceptionLog.AiAnalyze";
        public const string AiFollowUp = "ExceptionLog.AiFollowUp";
        public const string AiExportPdf = "ExceptionLog.AiExportPdf";
        public const string Delete = "ExceptionLog.Delete";
        public const string Purge = "ExceptionLog.Purge";
        public const string ClearAll = "ExceptionLog.ClearAll";
        public const string Export = "ExceptionLog.Export";
        public const string AutoPurge = "ExceptionLog.AutoPurge";
    }

    public static class AiCallLog
    {
        public const string Export = "AiCallLog.Export";
        public const string Delete = "AiCallLog.Delete";
        public const string Purge = "AiCallLog.Purge";
        public const string ClearAll = "AiCallLog.ClearAll";

        /// <summary>排程作業依保留天數自動清除（0.9.96 起；之前自動清除不寫稽核）。</summary>
        public const string AutoPurge = "AiCallLog.AutoPurge";
    }

    public static class TokenUsage
    {
        public const string Delete = "TokenUsage.Delete";
        public const string Purge = "TokenUsage.Purge";
        public const string ClearAll = "TokenUsage.ClearAll";
        public const string Export = "TokenUsage.Export";

        /// <summary>排程作業依保留天數自動清除（0.9.96 起；之前不會自動清除）。</summary>
        public const string AutoPurge = "TokenUsage.AutoPurge";
    }

    public static class Audit
    {
        public const string Purge = "Audit.Purge";
        public const string ClearAll = "Audit.ClearAll";
        public const string Export = "Audit.Export";
        public const string AutoPurge = "Audit.AutoPurge";
    }

    public static class Email
    {
        public const string Test = "Email.Test";
    }

    /// <summary>「排程作業」頁的手動操作（0.9.96 起）。排程自己跑的結果記在執行紀錄，不寫稽核。</summary>
    public static class Job
    {
        public const string Trigger = "Job.Trigger";
        public const string Enable = "Job.Enable";
        public const string Disable = "Job.Disable";
    }

    /// <summary>公告管理（0.9.100 起）。</summary>
    public static class Announcement
    {
        public const string Create = "Announcement.Create";
        public const string Update = "Announcement.Update";
        public const string Delete = "Announcement.Delete";
    }

    /// <summary>AI 用量上限（0.9.109 起）：送出前被上限擋下（detail 記作業、上限與已用金額）。</summary>
    public static class Ai
    {
        public const string QuotaBlocked = "Ai.QuotaBlocked";
    }

    /// <summary>AI 提示詞（0.9.108 起）：detail 只記範本與版本，不記提示詞內容。</summary>
    public static class Prompt
    {
        public const string Update = "Prompt.Update";
        public const string Activate = "Prompt.Activate";
    }

    /// <summary>站內通知（0.9.100 起）：排程作業依保留天數自動清除。</summary>
    public static class Notification
    {
        public const string AutoPurge = "Notification.AutoPurge";
    }

    /// <summary>系統備份（0.9.99 起）：建立（排程或立即備份）、依份數自動刪除、下載、手動刪除。</summary>
    public static class Backup
    {
        public const string Create = "Backup.Create";
        public const string AutoPurge = "Backup.AutoPurge";
        public const string Download = "Backup.Download";
        public const string Delete = "Backup.Delete";
    }

    /// <summary>「系統參數」頁的修改與還原（0.9.98 起）。Detail 為 <c>key=…; old=…(來源); new=…(來源)</c>。</summary>
    public static class SystemParameter
    {
        public const string Update = "SystemParameter.Update";
        public const string Reset = "SystemParameter.Reset";
    }
}
