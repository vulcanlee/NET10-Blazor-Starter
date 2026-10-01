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
    }

    public const string Logout = "Logout";

    public static class User
    {
        public const string Create = "User.Create";
        public const string Update = "User.Update";
        public const string Delete = "User.Delete";
        public const string SsoCreate = "User.SsoCreate";
        public const string SsoLink = "User.SsoLink";
    }

    public static class Role
    {
        public const string Create = "Role.Create";
        public const string Update = "Role.Update";
        public const string Delete = "Role.Delete";
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
        public const string Update = "Project.Update";
        public const string Delete = "Project.Delete";
        public const string FileUpload = "Project.FileUpload";
        public const string FileDelete = "Project.FileDelete";
        public const string FileDownload = "Project.FileDownload";
    }

    public static class Category
    {
        public const string Create = "Category.Create";
        public const string Update = "Category.Update";
        public const string Delete = "Category.Delete";
    }

    public static class Team
    {
        public const string Create = "Team.Create";
        public const string Update = "Team.Update";
        public const string Delete = "Team.Delete";
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
    }

    public static class TokenUsage
    {
        public const string Delete = "TokenUsage.Delete";
        public const string Purge = "TokenUsage.Purge";
        public const string ClearAll = "TokenUsage.ClearAll";
        public const string Export = "TokenUsage.Export";
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
}
