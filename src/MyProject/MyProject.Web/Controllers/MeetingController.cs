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
[ApiValidationFilter]
public class MeetingController : ControllerBase
{
    private readonly ILogger<MeetingController> logger;
    private readonly MeetingRepository meetingRepository;
    private readonly IMapper mapper;

    public MeetingController(
        ILogger<MeetingController> logger,
        MeetingRepository meetingRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.meetingRepository = meetingRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResult<MeetingDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        try
        {
            logger.LogDebug(
                "Received meeting get request. MeetingId={MeetingId}, IncludeRelatedData={IncludeRelatedData}",
                id,
                includeRelatedData);

            var meeting = await meetingRepository.GetByIdAsync(id, includeRelatedData);
            if (meeting == null)
            {
                logger.LogWarning("Meeting get request could not find record. MeetingId={MeetingId}", id);
                return NotFound(ApiResult<MeetingDto>.NotFoundResult($"找不到 ID 為 {id} 的會議"));
            }

            var meetingDto = mapper.Map<MeetingDto>(meeting);
            logger.LogInformation("Meeting retrieved successfully. MeetingId={MeetingId}", id);
            return Ok(ApiResult<MeetingDto>.SuccessResult(meetingDto, "取得會議成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get meeting. MeetingId={MeetingId}", id);
            return StatusCode(500, ApiResult<MeetingDto>.ServerErrorResult("取得會議失敗", ex.Message));
        }
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<MeetingDto>>>> Search([FromBody] MeetingSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received meeting search request. Keyword={Keyword}, ParticipantsKeyword={ParticipantsKeyword}, ProjectId={ProjectId}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, IncludeRelatedData={IncludeRelatedData}",
                request.Keyword,
                request.ParticipantsKeyword,
                request.ProjectId,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending,
                request.IncludeRelatedData);

            var pagedResult = await meetingRepository.GetPagedAsync(request, request.IncludeRelatedData);
            var meetingDtos = mapper.Map<List<MeetingDto>>(pagedResult.Items);

            var result = new PagedResult<MeetingDto>
            {
                Items = meetingDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Meeting search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}, PageIndex={PageIndex}, PageSize={PageSize}",
                result.Items.Count,
                result.TotalCount,
                result.PageIndex,
                result.PageSize);

            return Ok(ApiResult<PagedResult<MeetingDto>>.SuccessResult(result, "搜尋會議成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search meetings");
            return StatusCode(500, ApiResult<PagedResult<MeetingDto>>.ServerErrorResult("搜尋會議失敗", ex.Message));
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResult<MeetingDto>>> Create([FromBody] MeetingCreateUpdateDto meetingDto)
    {
        try
        {
            logger.LogDebug(
                "Received meeting create request. Title={Title}, ProjectId={ProjectId}",
                meetingDto.Title,
                meetingDto.ProjectId);

            if (await meetingRepository.ExistsByNameAsync(meetingDto.Title))
            {
                logger.LogWarning(
                    "Meeting create request rejected because title already exists. Title={Title}",
                    meetingDto.Title);
                return Conflict(ApiResult<MeetingDto>.ConflictResult($"會議名稱 '{meetingDto.Title}' 已存在"));
            }

            var meeting = mapper.Map<Meeting>(meetingDto);
            var createdMeeting = await meetingRepository.AddAsync(meeting);
            var createdMeetingDto = mapper.Map<MeetingDto>(createdMeeting);

            logger.LogInformation(
                "Meeting created successfully. MeetingId={MeetingId}, Title={Title}",
                createdMeetingDto.Id,
                createdMeetingDto.Title);

            return Ok(ApiResult<MeetingDto>.SuccessResult(createdMeetingDto, "新增會議成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create meeting. Title={Title}", meetingDto.Title);
            return StatusCode(500, ApiResult<MeetingDto>.ServerErrorResult("新增會議失敗", ex.Message));
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] MeetingCreateUpdateDto meetingDto)
    {
        try
        {
            logger.LogDebug(
                "Received meeting update request. RouteMeetingId={RouteMeetingId}, PayloadMeetingId={PayloadMeetingId}, Title={Title}",
                id,
                meetingDto.Id,
                meetingDto.Title);

            if (meetingDto.Id != id)
            {
                logger.LogWarning(
                    "Meeting update request rejected because route id and payload id do not match. RouteMeetingId={RouteMeetingId}, PayloadMeetingId={PayloadMeetingId}",
                    id,
                    meetingDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            if (await meetingRepository.ExistsByNameAsync(meetingDto.Title, id))
            {
                logger.LogWarning(
                    "Meeting update request rejected because title is already in use. MeetingId={MeetingId}, Title={Title}",
                    id,
                    meetingDto.Title);
                return Conflict(ApiResult.ConflictResult($"會議名稱 '{meetingDto.Title}' 已被其他會議使用"));
            }

            var meeting = mapper.Map<Meeting>(meetingDto);
            var success = await meetingRepository.UpdateAsync(meeting);
            if (!success)
            {
                logger.LogWarning("Meeting update request could not find record. MeetingId={MeetingId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的會議"));
            }

            logger.LogInformation("Meeting updated successfully. MeetingId={MeetingId}, Title={Title}", id, meetingDto.Title);
            return Ok(ApiResult.SuccessResult("更新會議成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update meeting. MeetingId={MeetingId}", id);
            return StatusCode(500, ApiResult.ServerErrorResult("更新會議失敗", ex.Message));
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received meeting delete request. MeetingId={MeetingId}", id);

            var success = await meetingRepository.DeleteAsync(id);
            if (!success)
            {
                logger.LogWarning("Meeting delete request could not find record. MeetingId={MeetingId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的會議"));
            }

            logger.LogInformation("Meeting deleted successfully. MeetingId={MeetingId}", id);
            return Ok(ApiResult.SuccessResult("刪除會議成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete meeting. MeetingId={MeetingId}", id);

            if (ex.InnerException?.Message.Contains("DELETE statement conflicted") == true)
            {
                logger.LogWarning(
                    "Meeting delete request rejected because related data still exists. MeetingId={MeetingId}",
                    id);
                return BadRequest(ApiResult.FailureResult("此會議仍有關聯資料，無法刪除"));
            }

            return StatusCode(500, ApiResult.ServerErrorResult("刪除會議失敗", ex.Message));
        }
    }
}
