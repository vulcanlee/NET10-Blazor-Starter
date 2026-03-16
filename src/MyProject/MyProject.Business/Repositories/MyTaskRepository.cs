using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Dtos.Commons;

namespace MyProject.Business.Repositories;

public class MyTaskRepository
{
    private readonly BackendDBContext context;

    public MyTaskRepository(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task<MyTas?> GetByIdAsync(int id, bool includeRelatedData = false)
    {
        IQueryable<MyTas> query = context.MyTas.AsNoTracking();

        if (includeRelatedData)
        {
            query = query
                .Include(x => x.Project)
                .Include(x => x.Files);
        }

        return await query.FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<MyTas>> GetPagedAsync(
        MyTaskSearchRequestDto request,
        bool includeRelatedData = false)
    {
        IQueryable<MyTas> query = context.MyTas.AsNoTracking();

        if (includeRelatedData)
        {
            query = query
                .Include(x => x.Project)
                .Include(x => x.Files);
        }

        Expression<Func<MyTas, bool>>? predicate = null;

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            predicate = x => x.Title.Contains(keyword) ||
                             (x.Description != null && x.Description.Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            Expression<Func<MyTas, bool>> categoryPredicate = x => x.Category == request.Category;
            predicate = CombinePredicates(predicate, categoryPredicate);
        }

        if (!string.IsNullOrWhiteSpace(request.Owner))
        {
            Expression<Func<MyTas, bool>> ownerPredicate = x => x.Owner == request.Owner;
            predicate = CombinePredicates(predicate, ownerPredicate);
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            Expression<Func<MyTas, bool>> statusPredicate = x => x.Status == request.Status;
            predicate = CombinePredicates(predicate, statusPredicate);
        }

        if (!string.IsNullOrWhiteSpace(request.Priority))
        {
            Expression<Func<MyTas, bool>> priorityPredicate = x => x.Priority == request.Priority;
            predicate = CombinePredicates(predicate, priorityPredicate);
        }

        if (request.ProjectId.HasValue)
        {
            Expression<Func<MyTas, bool>> projectPredicate = x => x.ProjectId == request.ProjectId.Value;
            predicate = CombinePredicates(predicate, projectPredicate);
        }

        if (request.StartDateFrom.HasValue)
        {
            Expression<Func<MyTas, bool>> startDateFromPredicate = x => x.StartDate >= request.StartDateFrom.Value;
            predicate = CombinePredicates(predicate, startDateFromPredicate);
        }

        if (request.StartDateTo.HasValue)
        {
            Expression<Func<MyTas, bool>> startDateToPredicate = x => x.StartDate <= request.StartDateTo.Value;
            predicate = CombinePredicates(predicate, startDateToPredicate);
        }

        if (request.CompletionPercentageMin.HasValue)
        {
            Expression<Func<MyTas, bool>> completionMinPredicate = x => x.CompletionPercentage >= request.CompletionPercentageMin.Value;
            predicate = CombinePredicates(predicate, completionMinPredicate);
        }

        if (request.CompletionPercentageMax.HasValue)
        {
            Expression<Func<MyTas, bool>> completionMaxPredicate = x => x.CompletionPercentage <= request.CompletionPercentageMax.Value;
            predicate = CombinePredicates(predicate, completionMaxPredicate);
        }

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        if (!string.IsNullOrWhiteSpace(request.SortBy))
        {
            query = request.SortBy.ToLowerInvariant() switch
            {
                "title" => request.SortDescending ? query.OrderByDescending(x => x.Title) : query.OrderBy(x => x.Title),
                "startdate" => request.SortDescending ? query.OrderByDescending(x => x.StartDate) : query.OrderBy(x => x.StartDate),
                "enddate" => request.SortDescending ? query.OrderByDescending(x => x.EndDate) : query.OrderBy(x => x.EndDate),
                "category" => request.SortDescending ? query.OrderByDescending(x => x.Category) : query.OrderBy(x => x.Category),
                "status" => request.SortDescending ? query.OrderByDescending(x => x.Status) : query.OrderBy(x => x.Status),
                "priority" => request.SortDescending ? query.OrderByDescending(x => x.Priority) : query.OrderBy(x => x.Priority),
                "completionpercentage" => request.SortDescending ? query.OrderByDescending(x => x.CompletionPercentage) : query.OrderBy(x => x.CompletionPercentage),
                "owner" => request.SortDescending ? query.OrderByDescending(x => x.Owner) : query.OrderBy(x => x.Owner),
                "projecttitle" => request.SortDescending
                    ? query.OrderByDescending(x => x.Project != null ? x.Project.Title : string.Empty)
                    : query.OrderBy(x => x.Project != null ? x.Project.Title : string.Empty),
                "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
                "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
                _ => query.OrderByDescending(x => x.UpdatedAt)
            };
        }
        else
        {
            query = query.OrderByDescending(x => x.UpdatedAt);
        }

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<MyTas>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ExistsByNameAsync(string title, int? excludeId = null)
    {
        var query = context.MyTas.Where(x => x.Title == title);

        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }

        return await query.AnyAsync();
    }

    public async Task<MyTas> AddAsync(MyTas task)
    {
        task.CreatedAt = DateTime.Now;
        task.UpdatedAt = DateTime.Now;

        await context.MyTas.AddAsync(task);
        await context.SaveChangesAsync();

        return task;
    }

    public async Task<bool> UpdateAsync(MyTas task)
    {
        var existingTask = await context.MyTas.FindAsync(task.Id);
        if (existingTask == null)
        {
            return false;
        }

        task.CreatedAt = existingTask.CreatedAt;
        task.UpdatedAt = DateTime.Now;

        context.Entry(existingTask).CurrentValues.SetValues(task);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var task = await context.MyTas.FindAsync(id);
        if (task == null)
        {
            return false;
        }

        context.MyTas.Remove(task);
        await context.SaveChangesAsync();
        return true;
    }

    private static Expression<Func<MyTas, bool>> CombinePredicates(
        Expression<Func<MyTas, bool>>? current,
        Expression<Func<MyTas, bool>> next)
    {
        if (current == null)
        {
            return next;
        }

        var parameter = Expression.Parameter(typeof(MyTas), "x");
        var leftBody = new ReplaceExpressionVisitor(current.Parameters[0], parameter).Visit(current.Body)!;
        var rightBody = new ReplaceExpressionVisitor(next.Parameters[0], parameter).Visit(next.Body)!;

        return Expression.Lambda<Func<MyTas, bool>>(Expression.AndAlso(leftBody, rightBody), parameter);
    }

    private sealed class ReplaceExpressionVisitor : ExpressionVisitor
    {
        private readonly Expression oldValue;
        private readonly Expression newValue;

        public ReplaceExpressionVisitor(Expression oldValue, Expression newValue)
        {
            this.oldValue = oldValue;
            this.newValue = newValue;
        }

        public override Expression? Visit(Expression? node)
        {
            return node == oldValue ? newValue : base.Visit(node);
        }
    }
}
