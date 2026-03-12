using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Share.Helpers;

namespace MyProject.Web.Controllers;

[ApiController]
[Route("api/project-files")]
[Authorize(AuthenticationSchemes = MagicObjectHelper.CookieScheme)]
public class ProjectFilesController : ControllerBase
{
    private readonly ILogger<ProjectFilesController> logger;
    private readonly ProjectService projectService;

    public ProjectFilesController(ILogger<ProjectFilesController> logger, ProjectService projectService)
    {
        this.logger = logger;
        this.projectService = projectService;
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> DownloadAsync(int id)
    {
        var file = await projectService.GetFileDownloadAsync(id);
        if (file is null)
        {
            logger.LogWarning("Project file download failed because the file was not found. ProjectFileId={ProjectFileId}", id);
            return NotFound();
        }

        logger.LogInformation("Project file download started. ProjectFileId={ProjectFileId}, FileName={FileName}", id, file.DownloadFileName);
        return File(file.Content, file.ContentType, file.DownloadFileName);
    }
}
