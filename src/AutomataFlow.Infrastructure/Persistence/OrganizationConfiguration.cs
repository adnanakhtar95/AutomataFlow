using AutomataFlow.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AutomataFlow.Infrastructure.Persistence;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Name).HasMaxLength(Organization.NameMaxLength).IsRequired();
        builder.Property(o => o.Slug).HasMaxLength(Organization.SlugMaxLength).IsRequired();
        builder.HasIndex(o => o.Slug).IsUnique();
    }
}
