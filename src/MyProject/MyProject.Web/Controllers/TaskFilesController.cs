using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Share.Helpers;

namespace MyProject.Web.Controllers;

[ApiController]
[Route("api/task-files")]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
public class TaskFilesController : ControllerBase
{
    private readonly ILogger<TaskFilesController> logger;
    private readonly MyTasService myTasService;

    public TaskFilesController(ILogger<TaskFilesController> logger, MyTasService myTasService)
    {
        this.logger = logger;
        this.myTasService = myTasService;
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> DownloadAsync(int id)
    {
        var file = await myTasService.GetFileDownloadAsync(id);
        if (file is null)
        {
            logger.LogWarning("Task file download failed because the file was not found. TaskFileId={TaskFileId}", id);
            return NotFound();
        }

        logger.LogInformation("Task file download started. TaskFileId={TaskFileId}, FileName={FileName}", id, file.DownloadFileName);
        return File(file.Content, file.ContentType, file.DownloadFileName);
    }
}
