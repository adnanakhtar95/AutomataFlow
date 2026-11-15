using AutomataFlow.Domain.Jobs;
using AutomataFlow.Domain.Organizations;
using AutomataFlow.Domain.Workflows;
using AutomataFlow.Infrastructure.Persistence.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AutomataFlow.Infrastructure.Persistence;

// Phase 5 step 1: the data model of docs/ARCHITECTURE.md §7. Every customer table has OrganizationId as the first
// column of its keys and indexes; foreign keys to a parent always include the organization, so a row can never point
// at another organization's parent even if the application layer were wrong.

internal sealed class WorkflowConfiguration : IEntityTypeConfiguration<Workflow>
{
    public void Configure(EntityTypeBuilder<Workflow> builder)
    {
        builder.ToTable("Workflows");
        builder.HasKey(w => w.Id);
        builder.HasAlternateKey(w => new { w.OrganizationId, w.Id });
        builder.Property(w => w.Name).HasMaxLength(Workflow.NameMaxLength).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(Workflow.DescriptionMaxLength);
        builder.Property(w => w.LatestVersion).IsConcurrencyToken();
        builder.HasIndex(w => new { w.OrganizationId, w.Name }).IsUnique();
        builder.HasOne<Organization>().WithMany().HasForeignKey(w => w.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersion>
{
    public void Configure(EntityTypeBuilder<WorkflowVersion> builder)
    {
        builder.ToTable("WorkflowVersions", t => t.HasCheckConstraint("CK_WorkflowVersions_Hash", "\"DefinitionHash\" ~ '^sha256:[0-9a-f]{64}$'"));
        builder.HasKey(v => v.Id);
        builder.HasAlternateKey(v => new { v.OrganizationId, v.Id });
        builder.Property(v => v.DefinitionJson).HasMaxLength(WorkflowVersion.MaxDefinitionChars).IsRequired();
        builder.Property(v => v.DefinitionHash).HasMaxLength(71).IsRequired();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(v => v.AcknowledgedAttestations).HasMaxLength(200);
        builder.HasIndex(v => new { v.OrganizationId, v.WorkflowId, v.VersionNumber }).IsUnique();
        builder.HasOne<Workflow>().WithMany().HasForeignKey(v => new { v.OrganizationId, v.WorkflowId })
            .HasPrincipalKey(w => new { w.OrganizationId, w.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkflowInputConfiguration : IEntityTypeConfiguration<WorkflowInput>
{
    public void Configure(EntityTypeBuilder<WorkflowInput> builder)
    {
        builder.ToTable("WorkflowInputs");
        builder.HasKey(i => new { i.OrganizationId, i.WorkflowVersionId, i.Name });
        builder.Property(i => i.Name).HasMaxLength(64).IsRequired();
        builder.Property(i => i.Type).HasMaxLength(16).IsRequired();
        builder.HasOne<WorkflowVersion>().WithMany().HasForeignKey(i => new { i.OrganizationId, i.WorkflowVersionId })
            .HasPrincipalKey(v => new { v.OrganizationId, v.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("Jobs");
        builder.HasKey(j => j.Id);
        builder.HasAlternateKey(j => new { j.OrganizationId, j.Id });
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.ComplexProperty(j => j.Options);
        builder.Property(j => j.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.IdempotencyKey).HasMaxLength(Job.IdempotencyKeyMaxLength);
        builder.Property(j => j.SourceName).HasMaxLength(Job.SourceNameMaxLength);
        builder.HasIndex(j => new { j.OrganizationId, j.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        builder.HasIndex(j => new { j.OrganizationId, j.CreatedAt });
        builder.HasIndex(j => j.Status).HasFilter("\"Status\" IN ('Pending', 'Running')");
        builder.HasOne<WorkflowVersion>().WithMany().HasForeignKey(j => new { j.OrganizationId, j.WorkflowVersionId })
            .HasPrincipalKey(v => new { v.OrganizationId, v.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JobRecordConfiguration : IEntityTypeConfiguration<JobRecord>
{
    public void Configure(EntityTypeBuilder<JobRecord> builder)
    {
        builder.ToTable("JobRecords", t =>
        {
            t.HasCheckConstraint("CK_JobRecords_Row", "\"RowNumber\" >= 1");
            t.HasCheckConstraint("CK_JobRecords_Attempts", "\"AttemptCount\" >= 0");
        });
        builder.HasKey(r => r.Id);
        builder.HasAlternateKey(r => new { r.OrganizationId, r.Id });
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.NextIntent).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.InputJson).IsRequired();
        builder.Property(r => r.ResultJson).HasColumnType("jsonb");
        builder.Property(r => r.ValidationErrorsJson).HasColumnType("jsonb");
        builder.Property(r => r.ErrorCode).HasMaxLength(JobRecord.ErrorCodeMaxLength);
        builder.Property(r => r.ErrorMessage).HasMaxLength(JobRecord.ErrorMessageMaxLength);
        builder.HasIndex(r => new { r.OrganizationId, r.OperationId }).IsUnique();
        builder.HasIndex(r => new { r.OrganizationId, r.JobId, r.RowNumber }).IsUnique();

        // The claim query (Worker): pending records due now, oldest first.
        builder.HasIndex(r => new { r.NextAttemptAt, r.CreatedAt }).HasFilter("\"Status\" = 'Pending'").HasDatabaseName("IX_JobRecords_Claimable");
        builder.HasIndex(r => r.LeaseExpiresAt).HasFilter("\"Status\" = 'Running'").HasDatabaseName("IX_JobRecords_Leased");
        builder.HasOne<Job>().WithMany().HasForeignKey(r => new { r.OrganizationId, r.JobId })
            .HasPrincipalKey(j => new { j.OrganizationId, j.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
{
    public void Configure(EntityTypeBuilder<JobAttempt> builder)
    {
        builder.ToTable("JobAttempts");
        builder.HasKey(a => a.Id);
        builder.HasAlternateKey(a => new { a.OrganizationId, a.Id });
        builder.Property(a => a.Intent).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.ErrorCode).HasMaxLength(64);
        builder.Property(a => a.ErrorCategory).HasMaxLength(32);
        builder.Property(a => a.StepsJson).HasColumnType("jsonb");
        builder.HasIndex(a => new { a.OrganizationId, a.JobRecordId, a.AttemptNumber }).IsUnique();
        builder.HasIndex(a => new { a.OrganizationId, a.JobId });
        builder.HasOne<JobRecord>().WithMany().HasForeignKey(a => new { a.OrganizationId, a.JobRecordId })
            .HasPrincipalKey(r => new { r.OrganizationId, r.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExecutionLogConfiguration : IEntityTypeConfiguration<ExecutionLog>
{
    public void Configure(EntityTypeBuilder<ExecutionLog> builder)
    {
        builder.ToTable("ExecutionLogs");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).UseIdentityAlwaysColumn();
        builder.Property(l => l.Level).HasMaxLength(16).IsRequired();
        builder.Property(l => l.EventType).HasMaxLength(64).IsRequired();
        builder.Property(l => l.StepId).HasMaxLength(64);
        builder.Property(l => l.Message).HasMaxLength(ExecutionLog.MessageMaxLength).IsRequired();
        builder.Property(l => l.PropertiesJson).HasColumnType("jsonb");
        builder.HasIndex(l => new { l.OrganizationId, l.JobAttemptId, l.Sequence }).IsUnique();
        builder.HasOne<JobAttempt>().WithMany().HasForeignKey(l => new { l.OrganizationId, l.JobAttemptId })
            .HasPrincipalKey(a => new { a.OrganizationId, a.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExecutionArtifactConfiguration : IEntityTypeConfiguration<ExecutionArtifact>
{
    public void Configure(EntityTypeBuilder<ExecutionArtifact> builder)
    {
        builder.ToTable("ExecutionArtifacts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Kind).HasMaxLength(32).IsRequired();
        builder.Property(a => a.StorageKey).HasMaxLength(300).IsRequired();
        builder.Property(a => a.FileName).HasMaxLength(255).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(127).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(a => new { a.OrganizationId, a.JobAttemptId });
        builder.HasIndex(a => a.StorageKey).IsUnique();
        builder.HasOne<JobAttempt>().WithMany().HasForeignKey(a => new { a.OrganizationId, a.JobAttemptId })
            .HasPrincipalKey(a => new { a.OrganizationId, a.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("UsageRecords");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Metric).HasMaxLength(64).IsRequired();
        builder.HasIndex(u => new { u.OrganizationId, u.RecordedAt });
        builder.HasOne<Job>().WithMany().HasForeignKey(u => new { u.OrganizationId, u.JobId })
            .HasPrincipalKey(j => new { j.OrganizationId, j.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationSecretConfiguration : IEntityTypeConfiguration<OrganizationSecret>
{
    public void Configure(EntityTypeBuilder<OrganizationSecret> builder)
    {
        builder.ToTable("OrganizationSecrets");
        builder.HasKey(s => new { s.OrganizationId, s.Name });
        builder.Property(s => s.Name).HasMaxLength(OrganizationSecret.NameMaxLength).IsRequired();
        builder.Property(s => s.ProtectedValue).HasMaxLength(8_000).IsRequired();
        builder.HasOne<Organization>().WithMany().HasForeignKey(s => s.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> builder)
    {
        builder.ToTable("Integrations");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Type).HasMaxLength(32).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(100).IsRequired();
        builder.Property(i => i.ProtectedConfig).IsRequired();
        builder.HasIndex(i => new { i.OrganizationId, i.Name }).IsUnique();
        builder.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AttemptJournalConfiguration : IEntityTypeConfiguration<AttemptJournalRow>
{
    public void Configure(EntityTypeBuilder<AttemptJournalRow> builder)
    {
        builder.ToTable("AttemptJournal", t =>
        {
            t.HasCheckConstraint("CK_AttemptJournal_State", "\"State\" IN ('Reserved', 'Started', 'Completed', 'Abandoned')");
            t.HasCheckConstraint("CK_AttemptJournal_Attempt", "\"AttemptNumber\" >= 1");
        });
        builder.HasKey(e => new { e.OrganizationId, e.OperationId, e.AttemptNumber });
        builder.Property(e => e.Intent).HasMaxLength(16).IsRequired();
        builder.Property(e => e.State).HasMaxLength(16).IsRequired();
        builder.Property(e => e.SnapshotJson).HasColumnType("jsonb");
        builder.Property(e => e.CheckpointStep).HasMaxLength(64);
        builder.Property(e => e.Outcome).HasMaxLength(32);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.ErrorCategory).HasMaxLength(32);
        builder.Property(e => e.CompletionSideEffect).HasMaxLength(16);
        builder.Property(e => e.FenceSource).HasMaxLength(32);
        builder.Property(e => e.FenceEvidenceJson).HasColumnType("jsonb");
        builder.Property(e => e.ReconciliationJson).HasColumnType("jsonb");
        builder.Property(e => e.FencingToken).UseIdentityAlwaysColumn();

        // ADR-049 / R6: at most ONE entry per operation that is Reserved, Started or unfenced, enforced by the database.
        builder.HasIndex(e => new { e.OrganizationId, e.OperationId }).IsUnique().HasFilter("\"Blocking\"").HasDatabaseName("UX_AttemptJournal_OneBlocking");
        builder.HasIndex(e => e.IncarnationId);
        builder.HasIndex(e => e.FencingToken).IsUnique();
        builder.HasOne<WorkerIncarnationRow>().WithMany().HasForeignKey(e => e.IncarnationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkerIncarnationConfiguration : IEntityTypeConfiguration<WorkerIncarnationRow>
{
    public void Configure(EntityTypeBuilder<WorkerIncarnationRow> builder)
    {
        builder.ToTable("WorkerIncarnations");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.HostId).HasMaxLength(255).IsRequired();
        builder.Property(w => w.BootId).HasMaxLength(64).IsRequired();
        builder.Property(w => w.UnitKind).HasMaxLength(32).IsRequired();
        builder.Property(w => w.UnitName).HasMaxLength(512).IsRequired();
        builder.Property(w => w.ConnectionLedger).HasMaxLength(1024).IsRequired();
    }
}

internal sealed class KillRequestConfiguration : IEntityTypeConfiguration<KillRequestRow>
{
    public void Configure(EntityTypeBuilder<KillRequestRow> builder)
    {
        builder.ToTable("WorkerKillRequests");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).UseIdentityAlwaysColumn();
        builder.HasIndex(k => k.IncarnationId);
        builder.HasOne<WorkerIncarnationRow>().WithMany().HasForeignKey(k => k.IncarnationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FenceConfirmationConfiguration : IEntityTypeConfiguration<FenceConfirmationRow>
{
    public void Configure(EntityTypeBuilder<FenceConfirmationRow> builder)
    {
        builder.ToTable("FenceConfirmations");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).UseIdentityAlwaysColumn();
        builder.Property(f => f.Source).HasMaxLength(32).IsRequired();
        builder.Property(f => f.Method).HasMaxLength(48).IsRequired();
        builder.Property(f => f.ActorUserId).HasMaxLength(64);
        builder.Property(f => f.EvidenceReference).HasMaxLength(1_000);
        builder.Property(f => f.RejectionReason).HasMaxLength(64);
        builder.Property(f => f.EvidenceJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(f => f.IncarnationId);
    }
}
