using System.Linq.Expressions;
using AutomataFlow.Domain.Common;
using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Identity;
using AutomataFlow.Infrastructure.Persistence.Journal;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AutomataFlow.Infrastructure.Persistence;

/// <summary>
/// The platform database (docs/ARCHITECTURE.md §7). Tenant isolation layers 3 and 4 (§7.1) live here:
/// <list type="bullet">
/// <item>a global query filter on every <see cref="IOrganizationOwned"/> entity restricts reads to the
/// <see cref="TenantScope"/>'s organization (no scope = no rows). It is a <b>backstop</b>: application services always
/// pass the organization explicitly;</item>
/// <item>a write guard in <c>SaveChanges</c> refuses to add, modify or delete an organization-owned row of any other
/// organization, or any such row without a scope.</item>
/// </list>
/// The attempt journal tables are written only with explicit SQL by the journal and carry no filter.
/// </summary>
public sealed class AutomataFlowDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly TenantScope _tenant;

    public AutomataFlowDbContext(DbContextOptions<AutomataFlowDbContext> options, TenantScope tenant)
        : base(options)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
    }

    /// <summary>
    /// Test hook (internal): runs the context with the query-filter backstop OFF, to prove that the application layer
    /// enforces organization scoping on its own (§7.1 layer 6). The write guard stays on.
    /// </summary>
    internal bool QueryFiltersDisabled { get; set; }

    /// <summary>Read by the query filters per context instance; <see cref="Guid.Empty"/> matches no row.</summary>
    internal Guid FilterOrganizationId => _tenant.OrganizationId ?? Guid.Empty;

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<OrganizationSecret> OrganizationSecrets => Set<OrganizationSecret>();

    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();

    public DbSet<Integration> Integrations => Set<Integration>();

    public DbSet<Workflow> Workflows => Set<Workflow>();

    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();

    public DbSet<WorkflowInput> WorkflowInputs => Set<WorkflowInput>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobRecord> JobRecords => Set<JobRecord>();

    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();

    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();

    public DbSet<ExecutionArtifact> ExecutionArtifacts => Set<ExecutionArtifact>();

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    internal DbSet<AttemptJournalRow> AttemptJournal => Set<AttemptJournalRow>();

    internal DbSet<WorkerIncarnationRow> WorkerIncarnations => Set<WorkerIncarnationRow>();

    internal DbSet<FenceConfirmationRow> FenceConfirmations => Set<FenceConfirmationRow>();

    /// <summary>Entities that carry an OrganizationId but are deliberately not filtered (explicit-SQL journal tables).</summary>
    internal static readonly IReadOnlySet<Type> UnfilteredOrganizationOwned = new HashSet<Type> { typeof(AttemptJournalRow) };

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AutomataFlowDbContext).Assembly);

        foreach (var entity in builder.Model.GetEntityTypes())
        {
            if (typeof(IOrganizationOwned).IsAssignableFrom(entity.ClrType) && !UnfilteredOrganizationOwned.Contains(entity.ClrType))
            {
                entity.SetQueryFilter(BuildFilter(entity.ClrType));
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardWrites()
    {
        foreach (var entry in ChangeTracker.Entries<IOrganizationOwned>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (_tenant.OrganizationId is not { } organization)
            {
                throw new TenantIsolationException($"Writing {entry.Metadata.ClrType.Name} requires an organization scope.");
            }

            var original = entry.State == EntityState.Added ? entry.Entity.OrganizationId : (Guid)entry.OriginalValues[nameof(IOrganizationOwned.OrganizationId)]!;
            if (entry.Entity.OrganizationId != organization || original != organization)
            {
                throw new TenantIsolationException($"Refused to write {entry.Metadata.ClrType.Name} of another organization.");
            }
        }
    }

    // e => this.QueryFiltersDisabled || e.OrganizationId == this.FilterOrganizationId  (evaluated per context instance)
    private LambdaExpression BuildFilter(Type entityType)
    {
        var entity = Expression.Parameter(entityType, "e");
        var context = Expression.Constant(this);
        var property = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(Guid));
        var owned = Expression.Call(property, entity, Expression.Constant(nameof(IOrganizationOwned.OrganizationId)));
        var scoped = Expression.Equal(owned, Expression.Property(context, nameof(FilterOrganizationId)));
        var body = Expression.OrElse(Expression.Property(context, nameof(QueryFiltersDisabled)), scoped);
        return Expression.Lambda(body, entity);
    }
}
