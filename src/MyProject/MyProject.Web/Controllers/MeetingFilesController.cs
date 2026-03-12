using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Share.Helpers;

namespace MyProject.Web.Controllers;

[ApiController]
[Route("api/meeting-files")]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
public class MeetingFilesController : ControllerBase
{
    private readonly ILogger<MeetingFilesController> logger;
    private readonly MeetingService meetingService;

    public MeetingFilesController(ILogger<MeetingFilesController> logger, MeetingService meetingService)
    {
        this.logger = logger;
        this.meetingService = meetingService;
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> DownloadAsync(int id)
    {
        var file = await meetingService.GetFileDownloadAsync(id);
        if (file is null)
        {
            logger.LogWarning("Meeting file download failed because the file was not found. MeetingFileId={MeetingFileId}", id);
            return NotFound();
        }

        logger.LogInformation("Meeting file download started. MeetingFileId={MeetingFileId}, FileName={FileName}", id, file.DownloadFileName);
        return File(file.Content, file.ContentType, file.DownloadFileName);
    }
}
