using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Dtos.Commons;

namespace MyProject.Business.Repositories;

public class MeetingRepository
{
    private readonly BackendDBContext context;

    public MeetingRepository(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task<Meeting?> GetByIdAsync(int id, bool includeRelatedData = false)
    {
        IQueryable<Meeting> query = context.Meeting.AsNoTracking();

        if (includeRelatedData)
        {
            query = query
                .Include(x => x.Project)
                .Include(x => x.Files);
        }

        return await query.FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Meeting>> GetPagedAsync(
        MeetingSearchRequestDto request,
        bool includeRelatedData = false)
    {
        IQueryable<Meeting> query = context.Meeting.AsNoTracking();

        if (includeRelatedData || string.Equals(request.SortBy, "projecttitle", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Include(x => x.Project);
        }

        if (includeRelatedData)
        {
            query = query.Include(x => x.Files);
        }

        Expression<Func<Meeting, bool>>? predicate = null;

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            predicate = x => x.Title.Contains(keyword) ||
                             (x.Description != null && x.Description.Contains(keyword)) ||
                             (x.Summary != null && x.Summary.Contains(keyword));
        }

        if (request.ProjectId.HasValue)
        {
            Expression<Func<Meeting, bool>> projectPredicate = x => x.ProjectId == request.ProjectId.Value;
            predicate = CombinePredicates(predicate, projectPredicate);
        }

        if (request.StartDateFrom.HasValue)
        {
            Expression<Func<Meeting, bool>> startDateFromPredicate = x => x.StartDate >= request.StartDateFrom.Value;
            predicate = CombinePredicates(predicate, startDateFromPredicate);
        }

        if (request.StartDateTo.HasValue)
        {
            Expression<Func<Meeting, bool>> startDateToPredicate = x => x.StartDate <= request.StartDateTo.Value;
            predicate = CombinePredicates(predicate, startDateToPredicate);
        }

        if (!string.IsNullOrWhiteSpace(request.ParticipantsKeyword))
        {
            var participantsKeyword = request.ParticipantsKeyword.Trim();
            Expression<Func<Meeting, bool>> participantsPredicate = x =>
                x.Participants != null && x.Participants.Contains(participantsKeyword);
            predicate = CombinePredicates(predicate, participantsPredicate);
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

        return new PagedResult<Meeting>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ExistsByNameAsync(string title, int? excludeId = null)
    {
        var query = context.Meeting.Where(x => x.Title == title);

        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }

        return await query.AnyAsync();
    }

    public async Task<Meeting> AddAsync(Meeting meeting)
    {
        meeting.CreatedAt = DateTime.Now;
        meeting.UpdatedAt = DateTime.Now;

        await context.Meeting.AddAsync(meeting);
        await context.SaveChangesAsync();

        return meeting;
    }

    public async Task<bool> UpdateAsync(Meeting meeting)
    {
        var existingMeeting = await context.Meeting.FindAsync(meeting.Id);
        if (existingMeeting == null)
        {
            return false;
        }

        meeting.CreatedAt = existingMeeting.CreatedAt;
        meeting.UpdatedAt = DateTime.Now;

        context.Entry(existingMeeting).CurrentValues.SetValues(meeting);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var meeting = await context.Meeting.FindAsync(id);
        if (meeting == null)
        {
            return false;
        }

        context.Meeting.Remove(meeting);
        await context.SaveChangesAsync();
        return true;
    }

    private static Expression<Func<Meeting, bool>> CombinePredicates(
        Expression<Func<Meeting, bool>>? current,
        Expression<Func<Meeting, bool>> next)
    {
        if (current == null)
        {
            return next;
        }

        var parameter = Expression.Parameter(typeof(Meeting), "x");
        var leftBody = new ReplaceExpressionVisitor(current.Parameters[0], parameter).Visit(current.Body)!;
        var rightBody = new ReplaceExpressionVisitor(next.Parameters[0], parameter).Visit(next.Body)!;

        return Expression.Lambda<Func<Meeting, bool>>(Expression.AndAlso(leftBody, rightBody), parameter);
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
