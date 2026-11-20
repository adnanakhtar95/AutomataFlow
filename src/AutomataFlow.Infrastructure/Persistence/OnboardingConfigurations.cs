using AutomataFlow.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AutomataFlow.Infrastructure.Persistence;

/// <summary>Workspace onboarding (Phase 8): invitations. Organization status is mapped in <see cref="OrganizationConfiguration"/>.</summary>
internal sealed class OrganizationInvitationConfiguration : IEntityTypeConfiguration<OrganizationInvitation>
{
    public void Configure(EntityTypeBuilder<OrganizationInvitation> builder)
    {
        builder.ToTable("OrganizationInvitations");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Email).HasMaxLength(OrganizationInvitation.EmailMaxLength).IsRequired();
        builder.Property(i => i.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();

        builder.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // Lookup by token happens before anyone is signed in; the hash is unique platform-wide.
        builder.HasIndex(i => i.TokenHash).IsUnique();
        builder.HasIndex(i => new { i.OrganizationId, i.Email });
    }
}
