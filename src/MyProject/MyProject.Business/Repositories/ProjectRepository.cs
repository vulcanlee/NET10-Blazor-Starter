using Microsoft.EntityFrameworkCore;
using MyProject.Business.Helpers;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers.Searchs;
using MyProject.Dtos.Commons;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MyProject.Business.Repositories;

public class ProjectRepository
{
    private readonly BackendDBContext context;
    private readonly ILogger<ProjectRepository> logger;

    public ProjectRepository(BackendDBContext context, ILogger<ProjectRepository> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    #region 查詢方法

    /// <summary>
    /// 根據 ID 取得專案
    /// </summary>
    public async Task<Project?> GetByIdAsync(int id, bool includeRelatedData = false)
    {
        var query = context.Project.AsNoTracking().AsQueryable();

        if (includeRelatedData)
        {
            // Project currently has no related data included by this API shape.
        }

        return await query.FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<PagedResult<Project>> GetPagedAsync(
        ProjectSearchRequestDto request,
        bool includeRelatedData = false)
    {
        var query = context.Project.AsNoTracking().AsQueryable();

        #region 建立過濾條件
        Expression<Func<Project, bool>>? predicate = null;

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            predicate = p => p.Title.Contains(request.Keyword) ||
                            (p.Description != null && p.Description.Contains(request.Keyword));
        }

        if (!string.IsNullOrEmpty(request.Owner))
        {
            var ownerPredicate = (Expression<Func<Project, bool>>)(p => p.Owner == request.Owner);
            predicate = predicate == null ? ownerPredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, ownerPredicate);
        }

        if (string.IsNullOrEmpty(request.Status) == false)
        {
            var statusPredicate = (Expression<Func<Project, bool>>)(p => p.Status == request.Status);
            predicate = predicate == null ? statusPredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, statusPredicate);
        }

        if (string.IsNullOrEmpty(request.Priority) == false)
        {
            var priorityPredicate = (Expression<Func<Project, bool>>)(p => p.Priority == request.Priority);
            predicate = predicate == null ? priorityPredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, priorityPredicate);
        }

        if (request.StartDateFrom.HasValue)
        {
            var datePredicate = (Expression<Func<Project, bool>>)(p => p.StartDate >= request.StartDateFrom.Value);
            predicate = predicate == null ? datePredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, datePredicate);
        }

        if (request.StartDateTo.HasValue)
        {
            var datePredicate = (Expression<Func<Project, bool>>)(p => p.StartDate <= request.StartDateTo.Value);
            predicate = predicate == null ? datePredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, datePredicate);
        }

        if (request.CompletionPercentageMin.HasValue)
        {
            var completionPredicate = (Expression<Func<Project, bool>>)(p => p.CompletionPercentage >= request.CompletionPercentageMin.Value);
            predicate = predicate == null ? completionPredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, completionPredicate);
        }

        if (request.CompletionPercentageMax.HasValue)
        {
            var completionPredicate = (Expression<Func<Project, bool>>)(p => p.CompletionPercentage <= request.CompletionPercentageMax.Value);
            predicate = predicate == null ? completionPredicate : CombinedSearchHelper.ProjectCombinePredicates(predicate, completionPredicate);
        }
        #endregion 

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        #region 根據 request.SortBy 及  request.Descending 進行排序
        if (!string.IsNullOrEmpty(request.SortBy))
        {
            query = request.SortBy.ToLower() switch
            {
                "title" => request.SortDescending
                    ? query.OrderByDescending(p => p.Title)
                    : query.OrderBy(p => p.Title),
                "startdate" => request.SortDescending
                    ? query.OrderByDescending(p => p.StartDate)
                    : query.OrderBy(p => p.StartDate),
                "enddate" => request.SortDescending
                    ? query.OrderByDescending(p => p.EndDate)
                    : query.OrderBy(p => p.EndDate),
                "status" => request.SortDescending
                    ? query.OrderByDescending(p => p.Status)
                    : query.OrderBy(p => p.Status),
                "priority" => request.SortDescending
                    ? query.OrderByDescending(p => p.Priority)
                    : query.OrderBy(p => p.Priority),
                "completionpercentage" => request.SortDescending
                    ? query.OrderByDescending(p => p.CompletionPercentage)
                    : query.OrderBy(p => p.CompletionPercentage),
                "createdat" => request.SortDescending
                    ? query.OrderByDescending(p => p.CreatedAt)
                    : query.OrderBy(p => p.CreatedAt),
                "updatedat" => request.SortDescending
                    ? query.OrderByDescending(p => p.UpdatedAt)
                    : query.OrderBy(p => p.UpdatedAt),
                _ => query.OrderByDescending(p => p.UpdatedAt),
            };
        }
        #endregion

        var totalCount = await query.CountAsync();

        // 記在 Debug：這是排查「為什麼查不到資料」的第一手線索。
        // 只記筆數與分頁參數，不記關鍵字內容 —— 使用者的搜尋字串可能包含個資。
        logger.LogDebug(
            "Paged project query executed. PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, HasKeyword={HasKeyword}, TotalCount={TotalCount}",
            request.PageIndex, request.PageSize, request.SortBy, request.SortDescending,
            string.IsNullOrWhiteSpace(request.Keyword) == false, totalCount);

        if (includeRelatedData)
        {
            // Project currently has no related data included by this API shape.
        }

        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        PagedResult<Project> pagedResult = new()
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };

        return pagedResult;
    }

    /// <summary>
    /// 檢查專案名稱是否存在
    /// </summary>
    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var query = context.Project.Where(p => p.Title == name);

        if (excludeId.HasValue)
        {
            query = query.Where(p => p.Id != excludeId.Value);
        }

        return await query.AnyAsync();
    }

    #endregion

    #region 新增方法

    /// <summary>
    /// 新增專案
    /// </summary>
    public async Task<Project> AddAsync(Project project)
    {
        project.CreatedAt = DateTime.Now;
        project.UpdatedAt = DateTime.Now;

        // 不信任客戶端傳來的版本號（POST 會忽略它）；AutoMapper 從 DTO 映射時可能是 null。
        // 一律由資料庫配號：0.9.93 之前會直接採用客戶端傳來的 Id，與既有資料撞號時回 500（軟刪除的列也一直佔著 Id）。
        project.Id = 0;
        project.ConcurrencyStamp = ConcurrencyStampHelper.New();
        await context.Project.AddAsync(project);
        await context.SaveChangesAsync();

        return project;
    }

    /// <summary>
    /// 批次新增專案
    /// </summary>
    public async Task<int> AddRangeAsync(List<Project> projects)
    {
        var now = DateTime.Now;
        foreach (var project in projects)
        {
            project.CreatedAt = now;
            project.UpdatedAt = now;
        }

        foreach (var project in projects)
        {
            project.Id = 0;
            project.ConcurrencyStamp = ConcurrencyStampHelper.New();
        }

        await context.Project.AddRangeAsync(projects);
        return await context.SaveChangesAsync();
    }

    #endregion

    #region 更新方法

    /// <summary>
    /// 更新專案
    /// </summary>
    public async Task<bool> UpdateAsync(Project project)
    {
        var existingProject = await context.Project.FindAsync(project.Id);
        if (existingProject == null)
        {
            return false;
        }

        project.UpdatedAt = DateTime.Now;
        project.CreatedAt = existingProject.CreatedAt; // 保留原建立時間

        context.Entry(existingProject).CurrentValues.SetValues(project);
        // FindAsync 載入的 OriginalValue 是資料庫目前的版本號，必須改成客戶端帶來的值才比對得出衝突；
        // 衝突時 SaveChanges 丟 DbUpdateConcurrencyException，由 controller 轉成 409。
        ConcurrencyStampHelper.Apply(context.Entry(existingProject), project.ConcurrencyStamp);
        SoftDeleteHelper.ProtectFlags(context.Entry(existingProject));
        await context.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// 更新專案狀態
    /// </summary>
    public async Task<bool> UpdateStatusAsync(int id, string status)
    {
        var project = await context.Project.FindAsync(id);
        if (project == null)
        {
            return false;
        }

        project.Status = status;
        project.UpdatedAt = DateTime.Now;

        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 更新專案完成百分比
    /// </summary>
    public async Task<bool> UpdateCompletionPercentageAsync(int id, int percentage)
    {
        if (percentage < 0 || percentage > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage), "完成百分比必須介於 0 到 100 之間");
        }

        var project = await context.Project.FindAsync(id);
        if (project == null)
        {
            return false;
        }

        project.CompletionPercentage = percentage;
        project.UpdatedAt = DateTime.Now;

        await context.SaveChangesAsync();
        return true;
    }

    #endregion

    #region 刪除方法

    /// <summary>
    /// 刪除專案
    /// </summary>
    /// <summary>
    /// 軟刪除（0.9.94 起）：可在畫面的「顯示已刪除」中還原或永久刪除。
    /// FindAsync 會套用軟刪除過濾，已刪除的資料回 false（controller 回 404）。
    /// </summary>
    public async Task<bool> DeleteAsync(int id, string? actorAccount)
    {
        var project = await context.Project.FindAsync(id);
        if (project == null)
        {
            return false;
        }

        SoftDeleteHelper.MarkDeleted(project, actorAccount);
        await context.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// 批次刪除專案
    /// </summary>
    public async Task<int> DeleteRangeAsync(List<int> ids, string? actorAccount)
    {
        var projects = await context.Project
            .Where(p => ids.Contains(p.Id))
            .ToListAsync();

        // 軟刪除（0.9.94 起），與 DeleteAsync 一致。
        foreach (var project in projects)
        {
            SoftDeleteHelper.MarkDeleted(project, actorAccount);
        }

        return await context.SaveChangesAsync();
    }

    #endregion

    #region 統計方法
    #endregion
}
