using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutomataFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowPublicationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedAttestations",
                table: "WorkflowVersions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedBy",
                table: "WorkflowVersions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgedAttestations",
                table: "WorkflowVersions");

            migrationBuilder.DropColumn(
                name: "PublishedBy",
                table: "WorkflowVersions");
        }
    }
}
