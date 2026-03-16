using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using MyProject.AccessDatas.Models;
using MyProject.Business.Repositories;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
[ApiValidationFilter] // <- 新增這行（若使用全域註冊可省略)
public class ProjectController : ControllerBase
{
    private readonly ILogger<ProjectController> logger;
    private readonly ProjectRepository projectRepository;
    private readonly IMapper mapper;

    public ProjectController(ILogger<ProjectController> logger,
        ProjectRepository projectRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.projectRepository = projectRepository;
        this.mapper = mapper;
    }

    #region 查詢 API

    /// <summary>
    /// 根據 ID 取得專案
    /// </summary>
    /// <param name="id">專案 ID</param>
    /// <param name="includeRelatedData">是否包含關聯資料</param>
    /// <returns></returns>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResult<ProjectDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        try
        {
            var project = await projectRepository.GetByIdAsync(id, includeRelatedData);

            if (project == null)
            {
                return NotFound(ApiResult<ProjectDto>.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            var projectDto = mapper.Map<ProjectDto>(project);
            return Ok(ApiResult<ProjectDto>.SuccessResult(projectDto, "取得專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "取得專案 ID {Id} 時發生錯誤", id);
            return StatusCode(500, ApiResult<ProjectDto>.ServerErrorResult("取得專案時發生錯誤", ex.Message));
        }
    }

    /// <summary>
    /// 分頁查詢專案(支援排序、過濾)
    /// </summary>
    /// <param name="request">查詢請求參數</param>
    /// <returns></returns>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<ProjectDto>>>> Search([FromBody] ProjectSearchRequestDto request)
    {
        try
        {
            // 執行分頁查詢
            PagedResult<Project> pagedResult = await projectRepository.GetPagedAsync(request);
            var projectDtos = mapper.Map<List<ProjectDto>>(pagedResult.Items);

            var result = new PagedResult<ProjectDto>
            {
                Items = projectDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            return Ok(ApiResult<PagedResult<ProjectDto>>.SuccessResult(result, "搜尋專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "搜尋專案時發生錯誤");
            return StatusCode(500, ApiResult<PagedResult<ProjectDto>>.ServerErrorResult("搜尋專案時發生錯誤", ex.Message));
        }
    }

    #endregion

    #region 新增 API

    /// <summary>
    /// 新增專案
    /// </summary>
    /// <param name="projectDto">專案資料</param>
    /// <returns></returns>
    [HttpPost]
    public async Task<ActionResult<ApiResult<ProjectDto>>> Create([FromBody] ProjectCreateUpdateDto projectDto)
    {
        try
        {
            // 已由 ApiValidationFilter 處理 ModelState 驗證
            // 檢查專案名稱是否重複
            if (await projectRepository.ExistsByNameAsync(projectDto.Title))
            {
                return Conflict(ApiResult<ProjectDto>.ConflictResult($"專案名稱 '{projectDto.Title}' 已存在"));
            }

            var project = mapper.Map<Project>(projectDto);
            var createdProject = await projectRepository.AddAsync(project);
            var createdProjectDto = mapper.Map<ProjectDto>(createdProject);

            return Ok(ApiResult<ProjectDto>.SuccessResult(createdProjectDto, "新增專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新增專案時發生錯誤");
            return StatusCode(500, ApiResult<ProjectDto>.ServerErrorResult("新增專案時發生錯誤", ex.Message));
        }
    }

    #endregion

    #region 更新 API

    /// <summary>
    /// 更新專案
    /// </summary>
    /// <param name="id">專案 ID</param>
    /// <param name="projectDto">專案資料</param>
    /// <returns></returns>
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] ProjectCreateUpdateDto projectDto)
    {
        try
        {
            // 已由 ApiValidationFilter 處理 ModelState 驗證
            if (id != projectDto.Id)
            {
                return BadRequest(ApiResult.ValidationError("路由 ID 與專案 ID 不符"));
            }

            if (await projectRepository.ExistsByNameAsync(projectDto.Title, id))
            {
                return Conflict(ApiResult.ConflictResult($"專案名稱 '{projectDto.Title}' 已被其他專案使用"));
            }

            var project = mapper.Map<Project>(projectDto);
            var success = await projectRepository.UpdateAsync(project);

            if (!success)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            return Ok(ApiResult.SuccessResult("更新專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新專案 ID {Id} 時發生錯誤", id);
            return StatusCode(500, ApiResult.ServerErrorResult("更新專案時發生錯誤", ex.Message));
        }
    }

    #endregion

    #region 刪除 API

    /// <summary>
    /// 刪除專案
    /// </summary>
    /// <param name="id">專案 ID</param>
    /// <returns></returns>
    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            var success = await projectRepository.DeleteAsync(id);

            if (!success)
            {
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            return Ok(ApiResult.SuccessResult("刪除專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "刪除專案 ID {Id} 時發生錯誤", id);

            // 檢查是否為外鍵約束錯誤
            if (ex.InnerException?.Message.Contains("DELETE statement conflicted") == true)
            {
                return BadRequest(ApiResult.FailureResult("無法刪除此專案,因為有相關的子資料(任務、會議等)存在"));
            }

            return StatusCode(500, ApiResult.ServerErrorResult("刪除專案時發生錯誤", ex.Message));
        }
    }

    #endregion

}
