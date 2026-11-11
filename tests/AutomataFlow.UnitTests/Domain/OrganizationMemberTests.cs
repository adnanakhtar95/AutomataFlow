using AutomataFlow.Domain.Common;
using AutomataFlow.Domain.Organizations;

namespace AutomataFlow.UnitTests.Domain;

public class OrganizationMemberTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sets_all_fields_and_is_organization_owned()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var member = OrganizationMember.Create(orgId, userId, OrganizationRole.Operator, Now);

        Assert.Equal(orgId, member.OrganizationId);
        Assert.Equal(userId, member.UserId);
        Assert.Equal(OrganizationRole.Operator, member.Role);
        Assert.IsAssignableFrom<IOrganizationOwned>(member);
    }

    [Fact]
    public void Create_rejects_empty_ids()
    {
        Assert.Throws<ArgumentException>(() => OrganizationMember.Create(Guid.Empty, Guid.NewGuid(), OrganizationRole.Viewer, Now));
        Assert.Throws<ArgumentException>(() => OrganizationMember.Create(Guid.NewGuid(), Guid.Empty, OrganizationRole.Viewer, Now));
    }

    [Fact]
    public void Create_rejects_undefined_role()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OrganizationMember.Create(Guid.NewGuid(), Guid.NewGuid(), (OrganizationRole)999, Now));
    }

    [Fact]
    public void ChangeRole_updates_role_and_rejects_undefined_values()
    {
        var member = OrganizationMember.Create(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Viewer, Now);

        member.ChangeRole(OrganizationRole.Admin);

        Assert.Equal(OrganizationRole.Admin, member.Role);
        Assert.Throws<ArgumentOutOfRangeException>(() => member.ChangeRole((OrganizationRole)(-1)));
        Assert.Equal(OrganizationRole.Admin, member.Role);
    }
}
