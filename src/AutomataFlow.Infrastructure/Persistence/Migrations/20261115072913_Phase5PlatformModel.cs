using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AutomataFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5PlatformModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FenceConfirmations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    IncarnationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttestedPersonally = table.Column<bool>(type: "boolean", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FencedAttempts = table.Column<int>(type: "integer", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FenceConfirmations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProtectedConfig = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Integrations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationSecrets",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedValue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSecrets", x => new { x.OrganizationId, x.Name });
                    table.ForeignKey(
                        name: "FK_OrganizationSecrets_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkerIncarnations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BootId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessId = table.Column<int>(type: "integer", nullable: false),
                    ProcessStartTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnitKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UnitName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ConnectionLedger = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerIncarnations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LatestVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                    table.UniqueConstraint("AK_Workflows_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_Workflows_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttemptJournal",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Intent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IncarnationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Blocking = table.Column<bool>(type: "boolean", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CheckpointAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CheckpointStep = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AbandonedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorTransient = table.Column<bool>(type: "boolean", nullable: true),
                    CompletionSideEffect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ExecutionFenced = table.Column<bool>(type: "boolean", nullable: false),
                    FencedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FenceSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FenceEvidenceJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReconciliationJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttemptJournal", x => new { x.OrganizationId, x.OperationId, x.AttemptNumber });
                    table.CheckConstraint("CK_AttemptJournal_Attempt", "\"AttemptNumber\" >= 1");
                    table.CheckConstraint("CK_AttemptJournal_State", "\"State\" IN ('Reserved', 'Started', 'Completed', 'Abandoned')");
                    table.ForeignKey(
                        name: "FK_AttemptJournal_WorkerIncarnations_IncarnationId",
                        column: x => x.IncarnationId,
                        principalTable: "WorkerIncarnations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkerKillRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    IncarnationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerKillRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerKillRequests_WorkerIncarnations_IncarnationId",
                        column: x => x.IncarnationId,
                        principalTable: "WorkerIncarnations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    DefinitionHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowVersions", x => x.Id);
                    table.UniqueConstraint("AK_WorkflowVersions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_WorkflowVersions_Hash", "\"DefinitionHash\" ~ '^sha256:[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_WorkflowVersions_Workflows_OrganizationId_WorkflowId",
                        columns: x => new { x.OrganizationId, x.WorkflowId },
                        principalTable: "Workflows",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TotalRecords = table.Column<int>(type: "integer", nullable: false),
                    InvalidRecords = table.Column<int>(type: "integer", nullable: false),
                    Options_MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Options_MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
                    Options_RetryBackoffSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.UniqueConstraint("AK_Jobs_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_Jobs_WorkflowVersions_OrganizationId_WorkflowVersionId",
                        columns: x => new { x.OrganizationId, x.WorkflowVersionId },
                        principalTable: "WorkflowVersions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowInputs",
                columns: table => new
                {
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowInputs", x => new { x.OrganizationId, x.WorkflowVersionId, x.Name });
                    table.ForeignKey(
                        name: "FK_WorkflowInputs_WorkflowVersions_OrganizationId_WorkflowVers~",
                        columns: x => new { x.OrganizationId, x.WorkflowVersionId },
                        principalTable: "WorkflowVersions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowNumber = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    InputProtected = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NextIntent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ReconcileFailures = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ValidationErrorsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRecords", x => x.Id);
                    table.UniqueConstraint("AK_JobRecords_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_JobRecords_Attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_JobRecords_Row", "\"RowNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_JobRecords_Jobs_OrganizationId_JobId",
                        columns: x => new { x.OrganizationId, x.JobId },
                        principalTable: "Jobs",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageRecords_Jobs_OrganizationId_JobId",
                        columns: x => new { x.OrganizationId, x.JobId },
                        principalTable: "Jobs",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Intent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkerIncarnationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SideEffectStarted = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutionFenced = table.Column<bool>(type: "boolean", nullable: false),
                    StepsJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttempts", x => x.Id);
                    table.UniqueConstraint("AK_JobAttempts_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_JobAttempts_JobRecords_OrganizationId_JobRecordId",
                        columns: x => new { x.OrganizationId, x.JobRecordId },
                        principalTable: "JobRecords",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionArtifacts_JobAttempts_OrganizationId_JobAttemptId",
                        columns: x => new { x.OrganizationId, x.JobAttemptId },
                        principalTable: "JobAttempts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StepId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionLogs_JobAttempts_OrganizationId_JobAttemptId",
                        columns: x => new { x.OrganizationId, x.JobAttemptId },
                        principalTable: "JobAttempts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttemptJournal_FencingToken",
                table: "AttemptJournal",
                column: "FencingToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttemptJournal_IncarnationId",
                table: "AttemptJournal",
                column: "IncarnationId");

            migrationBuilder.CreateIndex(
                name: "UX_AttemptJournal_OneBlocking",
                table: "AttemptJournal",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true,
                filter: "\"Blocking\"");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionArtifacts_OrganizationId_JobAttemptId",
                table: "ExecutionArtifacts",
                columns: new[] { "OrganizationId", "JobAttemptId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionArtifacts_StorageKey",
                table: "ExecutionArtifacts",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionLogs_OrganizationId_JobAttemptId_Sequence",
                table: "ExecutionLogs",
                columns: new[] { "OrganizationId", "JobAttemptId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FenceConfirmations_IncarnationId",
                table: "FenceConfirmations",
                column: "IncarnationId");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_OrganizationId_Name",
                table: "Integrations",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_OrganizationId_JobId",
                table: "JobAttempts",
                columns: new[] { "OrganizationId", "JobId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_OrganizationId_JobRecordId_AttemptNumber",
                table: "JobAttempts",
                columns: new[] { "OrganizationId", "JobRecordId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRecords_Claimable",
                table: "JobRecords",
                columns: new[] { "NextAttemptAt", "CreatedAt" },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_JobRecords_Leased",
                table: "JobRecords",
                column: "LeaseExpiresAt",
                filter: "\"Status\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_JobRecords_OrganizationId_JobId_RowNumber",
                table: "JobRecords",
                columns: new[] { "OrganizationId", "JobId", "RowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRecords_OrganizationId_OperationId",
                table: "JobRecords",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OrganizationId_CreatedAt",
                table: "Jobs",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OrganizationId_IdempotencyKey",
                table: "Jobs",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OrganizationId_WorkflowVersionId",
                table: "Jobs",
                columns: new[] { "OrganizationId", "WorkflowVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status",
                filter: "\"Status\" IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_OrganizationId_JobId",
                table: "UsageRecords",
                columns: new[] { "OrganizationId", "JobId" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_OrganizationId_RecordedAt",
                table: "UsageRecords",
                columns: new[] { "OrganizationId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerKillRequests_IncarnationId",
                table: "WorkerKillRequests",
                column: "IncarnationId");

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_OrganizationId_Name",
                table: "Workflows",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowVersions_OrganizationId_WorkflowId_VersionNumber",
                table: "WorkflowVersions",
                columns: new[] { "OrganizationId", "WorkflowId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttemptJournal");

            migrationBuilder.DropTable(
                name: "ExecutionArtifacts");

            migrationBuilder.DropTable(
                name: "ExecutionLogs");

            migrationBuilder.DropTable(
                name: "FenceConfirmations");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropTable(
                name: "OrganizationSecrets");

            migrationBuilder.DropTable(
                name: "UsageRecords");

            migrationBuilder.DropTable(
                name: "WorkerKillRequests");

            migrationBuilder.DropTable(
                name: "WorkflowInputs");

            migrationBuilder.DropTable(
                name: "JobAttempts");

            migrationBuilder.DropTable(
                name: "WorkerIncarnations");

            migrationBuilder.DropTable(
                name: "JobRecords");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "WorkflowVersions");

            migrationBuilder.DropTable(
                name: "Workflows");
        }
    }
}
