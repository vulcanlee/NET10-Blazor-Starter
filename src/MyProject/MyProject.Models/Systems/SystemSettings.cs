namespace MyProject.Models.Systems;

public class SystemSettings
{
    public ConnectionStrings ConnectionStrings { get; set; }
    public SystemInformation SystemInformation { get; set; }
    public ExternalFileSystem ExternalFileSystem { get; set; }
}
public class ConnectionStrings
{
    public string DefaultConnection { get; set; }
    public string SQLiteDefaultConnection { get; set; }

}
public class SystemInformation
{
    public string SystemVersion { get; set; }
    public string SystemName { get; set; }
    public string SystemDescription { get; set; }
}
public class ExternalFileSystem
{
    public string DatabasePath { get; set; }
    public string DownloadPath { get; set; }
    public string UploadPath { get; set; }
    public string ProjectFilePath { get; set; }
    public string TaskFilePath { get; set; }
    public string MeetingFilePath { get; set; }
}
