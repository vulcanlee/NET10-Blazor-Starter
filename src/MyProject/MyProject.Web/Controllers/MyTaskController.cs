using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.AccessDatas.Models;
using MyProject.Business.Repositories;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class MyTaskController : ControllerBase
{
    private readonly ILogger<MyTaskController> logger;
    private readonly MyTaskRepository myTaskRepository;
    private readonly IMapper mapper;

    public MyTaskController(
        ILogger<MyTaskController> logger,
        MyTaskRepository myTaskRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.myTaskRepository = myTaskRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResult<MyTaskDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        try
        {
            logger.LogDebug(
                "Received task get request. TaskId={TaskId}, IncludeRelatedData={IncludeRelatedData}",
                id,
                includeRelatedData);

            var task = await myTaskRepository.GetByIdAsync(id, includeRelatedData);
            if (task == null)
            {
                logger.LogWarning("Task get request could not find record. TaskId={TaskId}", id);
                return NotFound(ApiResult<MyTaskDto>.NotFoundResult($"找不到 ID 為 {id} 的任務"));
            }

            var taskDto = mapper.Map<MyTaskDto>(task);
            logger.LogInformation("Task retrieved successfully. TaskId={TaskId}", id);

            return Ok(ApiResult<MyTaskDto>.SuccessResult(taskDto, "取得任務成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get task. TaskId={TaskId}", id);
            return StatusCode(500, ApiResult<MyTaskDto>.ServerErrorResult("取得任務失敗", ex));
        }
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<MyTaskDto>>>> Search([FromBody] MyTaskSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received task search request. Keyword={Keyword}, Category={Category}, Owner={Owner}, Status={Status}, Priority={Priority}, ProjectId={ProjectId}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, IncludeRelatedData={IncludeRelatedData}",
                request.Keyword,
                request.Category,
                request.Owner,
                request.Status,
                request.Priority,
                request.ProjectId,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending,
                request.IncludeRelatedData);

            var pagedResult = await myTaskRepository.GetPagedAsync(request, request.IncludeRelatedData);
            var taskDtos = mapper.Map<List<MyTaskDto>>(pagedResult.Items);

            var result = new PagedResult<MyTaskDto>
            {
                Items = taskDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Task search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}, PageIndex={PageIndex}, PageSize={PageSize}",
                result.Items.Count,
                result.TotalCount,
                result.PageIndex,
                result.PageSize);

            return Ok(ApiResult<PagedResult<MyTaskDto>>.SuccessResult(result, "搜尋任務成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search tasks");
            return StatusCode(500, ApiResult<PagedResult<MyTaskDto>>.ServerErrorResult("搜尋任務失敗", ex));
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResult<MyTaskDto>>> Create([FromBody] MyTaskCreateUpdateDto myTaskDto)
    {
        try
        {
            logger.LogDebug(
                "Received task create request. Title={Title}, Owner={Owner}, Status={Status}, Priority={Priority}, ProjectId={ProjectId}",
                myTaskDto.Title,
                myTaskDto.Owner,
                myTaskDto.Status,
                myTaskDto.Priority,
                myTaskDto.ProjectId);

            if (await myTaskRepository.ExistsByNameAsync(myTaskDto.Title))
            {
                logger.LogWarning(
                    "Task create request rejected because title already exists. Title={Title}",
                    myTaskDto.Title);
                return Conflict(ApiResult<MyTaskDto>.ConflictResult($"任務名稱 '{myTaskDto.Title}' 已存在"));
            }

            var task = mapper.Map<MyTask>(myTaskDto);
            var createdTask = await myTaskRepository.AddAsync(task);
            var createdTaskDto = mapper.Map<MyTaskDto>(createdTask);

            logger.LogInformation(
                "Task created successfully. TaskId={TaskId}, Title={Title}",
                createdTaskDto.Id,
                createdTaskDto.Title);

            return Ok(ApiResult<MyTaskDto>.SuccessResult(createdTaskDto, "新增任務成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create task. Title={Title}", myTaskDto.Title);
            return StatusCode(500, ApiResult<MyTaskDto>.ServerErrorResult("新增任務失敗", ex));
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] MyTaskCreateUpdateDto myTaskDto)
    {
        try
        {
            logger.LogDebug(
                "Received task update request. RouteTaskId={RouteTaskId}, PayloadTaskId={PayloadTaskId}, Title={Title}",
                id,
                myTaskDto.Id,
                myTaskDto.Title);

            if (myTaskDto.Id != id)
            {
                logger.LogWarning(
                    "Task update request rejected because route id and payload id do not match. RouteTaskId={RouteTaskId}, PayloadTaskId={PayloadTaskId}",
                    id,
                    myTaskDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await myTaskRepository.ExistsByNameAsync(myTaskDto.Title, id))
            {
                logger.LogWarning(
                    "Task update request rejected because title is already in use. TaskId={TaskId}, Title={Title}",
                    id,
                    myTaskDto.Title);
                return Conflict(ApiResult.ConflictResult($"任務名稱 '{myTaskDto.Title}' 已被其他任務使用"));
            }

            var task = mapper.Map<MyTask>(myTaskDto);
            var success = await myTaskRepository.UpdateAsync(task);
            if (!success)
            {
                logger.LogWarning("Task update request could not find record. TaskId={TaskId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的任務"));
            }

            logger.LogInformation("Task updated successfully. TaskId={TaskId}, Title={Title}", id, myTaskDto.Title);
            return Ok(ApiResult.SuccessResult("更新任務成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update task. TaskId={TaskId}", id);
            return StatusCode(500, ApiResult.ServerErrorResult("更新任務失敗", ex));
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received task delete request. TaskId={TaskId}", id);

            var success = await myTaskRepository.DeleteAsync(id);
            if (!success)
            {
                logger.LogWarning("Task delete request could not find record. TaskId={TaskId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的任務"));
            }

            logger.LogInformation("Task deleted successfully. TaskId={TaskId}", id);
            return Ok(ApiResult.SuccessResult("刪除任務成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete task. TaskId={TaskId}", id);

            if (ex.InnerException?.Message.Contains("DELETE statement conflicted") == true)
            {
                logger.LogWarning(
                    "Task delete request rejected because related data still exists. TaskId={TaskId}",
                    id);
                return BadRequest(ApiResult.FailureResult("此任務仍有關聯資料，無法刪除"));
            }

            return StatusCode(500, ApiResult.ServerErrorResult("刪除任務失敗", ex));
        }
    }
}
