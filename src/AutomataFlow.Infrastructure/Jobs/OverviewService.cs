using AutomataFlow.Application.Abstractions;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.Infrastructure.Jobs;

public sealed record RecentWorkflow(Guid Id, string Name, int LatestVersion, DateTimeOffset CreatedAt);

/// <summary>Workspace totals for the dashboard. Every count is computed in the database for the caller's organization only.</summary>
public sealed record OverviewView(
    int TotalJobs,
    IReadOnlyDictionary<string, int> JobsByStatus,
    int TotalRecords,
    IReadOnlyDictionary<string, int> RecordsByStatus,
    int TotalWorkflows,
    int PublishedWorkflows,
    IReadOnlyList<JobView> RecentJobs,
    IReadOnlyList<RecentWorkflow> RecentWorkflows);

/// <summary>Dashboard statistics (Phase 6). The organization comes from <see cref="ICurrentUser"/> and is in every query.</summary>
public sealed class OverviewService(AutomataFlowDbContext db, ICurrentUser user, JobService jobs)
{
    public const int RecentCount = 5;

    public async Task<OverviewView> GetAsync(CancellationToken cancellationToken)
    {
        var org = user.OrganizationId;
        var jobCounts = await db.Jobs.AsNoTracking().Where(j => j.OrganizationId == org)
            .GroupBy(j => j.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var recordCounts = await db.JobRecords.AsNoTracking().Where(r => r.OrganizationId == org)
            .GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var totalWorkflows = await db.Workflows.AsNoTracking().CountAsync(w => w.OrganizationId == org, cancellationToken);
        var publishedWorkflows = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.OrganizationId == org && v.Status == WorkflowVersionStatus.Published).Select(v => v.WorkflowId).Distinct().CountAsync(cancellationToken);
        var recentWorkflows = await db.Workflows.AsNoTracking().Where(w => w.OrganizationId == org).OrderByDescending(w => w.CreatedAt).Take(RecentCount)
            .Select(w => new RecentWorkflow(w.Id, w.Name, w.LatestVersion, w.CreatedAt)).ToListAsync(cancellationToken);

        return new OverviewView(
            jobCounts.Sum(c => c.Count),
            jobCounts.ToDictionary(c => c.Key.ToString(), c => c.Count, StringComparer.Ordinal),
            recordCounts.Sum(c => c.Count),
            recordCounts.ToDictionary(c => c.Key.ToString(), c => c.Count, StringComparer.Ordinal),
            totalWorkflows,
            publishedWorkflows,
            await jobs.ListAsync(null, 0, RecentCount, cancellationToken),
            recentWorkflows);
    }
}
