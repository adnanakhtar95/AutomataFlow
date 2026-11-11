using AutomataFlow.Domain.Organizations;
using AutomataFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AutomataFlow.Infrastructure.Persistence;

internal sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("OrganizationMembers");
        builder.HasKey(m => new { m.OrganizationId, m.UserId });
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(m => m.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // "Which organizations does this user belong to?" is queried across organizations during sign-in.
        builder.HasIndex(m => m.UserId);
    }
}
