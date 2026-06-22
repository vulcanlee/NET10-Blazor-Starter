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
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class TeamController : ControllerBase
{
    private readonly ILogger<TeamController> logger;
    private readonly TeamRepository teamRepository;
    private readonly IMapper mapper;

    public TeamController(
        ILogger<TeamController> logger,
        TeamRepository teamRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.teamRepository = teamRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResult<TeamDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received team get request. TeamId={TeamId}", id);

            var team = await teamRepository.GetByIdAsync(id);
            if (team == null)
            {
                logger.LogWarning("Team get request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult<TeamDto>.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            var teamDto = mapper.Map<TeamDto>(team);
            logger.LogInformation("Team retrieved successfully. TeamId={TeamId}", id);
            return Ok(ApiResult<TeamDto>.SuccessResult(teamDto, "取得團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get team. TeamId={TeamId}", id);
            return this.ApiServerError<TeamDto>("取得團隊失敗", ex);
        }
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<TeamDto>>>> Search([FromBody] TeamSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received team search request. Keyword={Keyword}, IsEnabled={IsEnabled}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                request.Keyword,
                request.IsEnabled,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var pagedResult = await teamRepository.GetPagedAsync(request);
            var teamDtos = mapper.Map<List<TeamDto>>(pagedResult.Items);

            var result = new PagedResult<TeamDto>
            {
                Items = teamDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Team search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<TeamDto>>.SuccessResult(result, "搜尋團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search teams");
            return this.ApiServerError<PagedResult<TeamDto>>("搜尋團隊失敗", ex);
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResult<TeamDto>>> Create([FromBody] TeamCreateUpdateDto teamDto)
    {
        try
        {
            logger.LogDebug("Received team create request. Name={Name}, Code={Code}", teamDto.Name, teamDto.Code);

            if (await teamRepository.ExistsByNameAsync(teamDto.Name))
            {
                logger.LogWarning("Team create request rejected because name already exists. Name={Name}", teamDto.Name);
                return Conflict(ApiResult<TeamDto>.ConflictResult($"團隊名稱 '{teamDto.Name}' 已存在"));
            }

            if (!string.IsNullOrWhiteSpace(teamDto.Code) && await teamRepository.ExistsByCodeAsync(teamDto.Code))
            {
                logger.LogWarning("Team create request rejected because code already exists. Code={Code}", teamDto.Code);
                return Conflict(ApiResult<TeamDto>.ConflictResult($"團隊代號 '{teamDto.Code}' 已存在"));
            }

            var team = mapper.Map<Team>(teamDto);
            var created = await teamRepository.AddAsync(team);
            var createdDto = mapper.Map<TeamDto>(created);

            logger.LogInformation("Team created successfully. TeamId={TeamId}, Name={Name}", createdDto.Id, createdDto.Name);
            return Ok(ApiResult<TeamDto>.SuccessResult(createdDto, "新增團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create team. Name={Name}", teamDto.Name);
            return this.ApiServerError<TeamDto>("新增團隊失敗", ex);
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] TeamCreateUpdateDto teamDto)
    {
        try
        {
            logger.LogDebug("Received team update request. RouteId={RouteId}, PayloadId={PayloadId}, Name={Name}", id, teamDto.Id, teamDto.Name);

            if (id != teamDto.Id)
            {
                logger.LogWarning("Team update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, teamDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await teamRepository.ExistsByNameAsync(teamDto.Name, id))
            {
                logger.LogWarning("Team update request rejected because name is already in use. TeamId={TeamId}, Name={Name}", id, teamDto.Name);
                return Conflict(ApiResult.ConflictResult($"團隊名稱 '{teamDto.Name}' 已被其他團隊使用"));
            }

            if (!string.IsNullOrWhiteSpace(teamDto.Code) && await teamRepository.ExistsByCodeAsync(teamDto.Code, id))
            {
                logger.LogWarning("Team update request rejected because code is already in use. TeamId={TeamId}, Code={Code}", id, teamDto.Code);
                return Conflict(ApiResult.ConflictResult($"團隊代號 '{teamDto.Code}' 已被其他團隊使用"));
            }

            var team = mapper.Map<Team>(teamDto);
            var success = await teamRepository.UpdateAsync(team);
            if (!success)
            {
                logger.LogWarning("Team update request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            logger.LogInformation("Team updated successfully. TeamId={TeamId}, Name={Name}", id, teamDto.Name);
            return Ok(ApiResult.SuccessResult("更新團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update team. TeamId={TeamId}", id);
            return this.ApiServerError("更新團隊失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received team delete request. TeamId={TeamId}", id);

            var success = await teamRepository.DeleteAsync(id);
            if (!success)
            {
                logger.LogWarning("Team delete request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            logger.LogInformation("Team deleted successfully. TeamId={TeamId}", id);
            return Ok(ApiResult.SuccessResult("刪除團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete team. TeamId={TeamId}", id);
            return this.ApiServerError("刪除團隊失敗", ex);
        }
    }
}
