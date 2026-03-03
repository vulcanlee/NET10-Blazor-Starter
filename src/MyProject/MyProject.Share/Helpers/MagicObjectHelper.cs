namespace MyProject.Share.Helpers;

public class MagicObjectHelper
{
    #region 系統層面用到神奇字串
    public const string DefaultSQLiteConnectionStringKey = "SQLiteDefaultConnection";
    public const string SystemSettings = "SystemSettings";
    public const string SQLiteDatabaseFilename = "BackendDB.db";
    public static string GetSQLiteConnectionString(string databasePath)
    {
        return $"Data Source={Path.Combine(databasePath, SQLiteDatabaseFilename)}";
    }
    #endregion
}
