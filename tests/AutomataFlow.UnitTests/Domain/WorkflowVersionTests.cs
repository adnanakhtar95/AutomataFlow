using AutomataFlow.Domain.Common;
using AutomataFlow.Domain.Workflows;

namespace AutomataFlow.UnitTests.Domain;

/// <summary>Phase 4 step 3: workflow version lifecycle (docs/PHASE4_PLAN.md §11, §16 D5).</summary>
public class WorkflowVersionTests
{
    private const string Hash = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string OtherHash = "sha256:3682ab466134d84fce99285304e23fea7e872a187b8f8e4d6a7946ae8031e943";
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static WorkflowVersion Draft() => WorkflowVersion.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "{}", Hash, T0);

    [Fact]
    public void A_new_version_is_an_organization_owned_draft()
    {
        var organizationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        var version = WorkflowVersion.CreateDraft(organizationId, workflowId, 3, 1, "{\"a\":1}", Hash, T0);

        Assert.IsAssignableFrom<IOrganizationOwned>(version);
        Assert.Equal(organizationId, version.OrganizationId);
        Assert.Equal(workflowId, version.WorkflowId);
        Assert.Equal(3, version.VersionNumber);
        Assert.Equal(1, version.SchemaVersion);
        Assert.Equal("{\"a\":1}", version.DefinitionJson);
        Assert.Equal(Hash, version.DefinitionHash);
        Assert.Equal(WorkflowVersionStatus.Draft, version.Status);
        Assert.Equal(T0, version.CreatedAt);
        Assert.Null(version.PublishedAt);
        Assert.Null(version.ArchivedAt);
        Assert.NotEqual(Guid.Empty, version.Id);
    }

    [Fact]
    public void Draft_to_published_to_archived()
    {
        var version = Draft();

        version.Publish(T0.AddMinutes(1));
        Assert.Equal(WorkflowVersionStatus.Published, version.Status);
        Assert.Equal(T0.AddMinutes(1), version.PublishedAt);

        version.Archive(T0.AddMinutes(2));
        Assert.Equal(WorkflowVersionStatus.Archived, version.Status);
        Assert.Equal(T0.AddMinutes(2), version.ArchivedAt);
    }

    [Fact]
    public void A_draft_can_be_edited()
    {
        var version = Draft();

        version.UpdateDraft(1, "{\"b\":2}", OtherHash);

        Assert.Equal("{\"b\":2}", version.DefinitionJson);
        Assert.Equal(OtherHash, version.DefinitionHash);
    }

    [Fact]
    public void Published_and_archived_versions_are_immutable()
    {
        var published = Draft();
        published.Publish(T0);
        Assert.Throws<InvalidOperationException>(() => published.UpdateDraft(1, "{\"b\":2}", OtherHash));
        Assert.Equal("{}", published.DefinitionJson);
        Assert.Equal(Hash, published.DefinitionHash);

        published.Archive(T0);
        Assert.Throws<InvalidOperationException>(() => published.UpdateDraft(1, "{\"b\":2}", OtherHash));
        Assert.Equal(Hash, published.DefinitionHash);
    }

    [Fact]
    public void There_are_no_backward_or_skipping_transitions()
    {
        var draft = Draft();
        Assert.Throws<InvalidOperationException>(() => draft.Archive(T0));
        Assert.Equal(WorkflowVersionStatus.Draft, draft.Status);

        var published = Draft();
        published.Publish(T0);
        Assert.Throws<InvalidOperationException>(() => published.Publish(T0));

        var archived = Draft();
        archived.Publish(T0);
        archived.Archive(T0);
        Assert.Throws<InvalidOperationException>(() => archived.Publish(T0));
        Assert.Throws<InvalidOperationException>(() => archived.Archive(T0));
        Assert.Equal(WorkflowVersionStatus.Archived, archived.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("sha1:da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void The_hash_format_is_enforced(string hash)
    {
        Assert.Throws<ArgumentException>(() => WorkflowVersion.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "{}", hash, T0));
        Assert.Throws<ArgumentException>(() => Draft().UpdateDraft(1, "{}", hash));
    }

    [Fact]
    public void Invalid_identity_and_content_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WorkflowVersion.CreateDraft(Guid.Empty, Guid.NewGuid(), 1, 1, "{}", Hash, T0));
        Assert.Throws<ArgumentException>(() => WorkflowVersion.CreateDraft(Guid.NewGuid(), Guid.Empty, 1, 1, "{}", Hash, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowVersion.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), 0, 1, "{}", Hash, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowVersion.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), 1, 0, "{}", Hash, T0));
        Assert.Throws<ArgumentException>(() => WorkflowVersion.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), 1, 1, " ", Hash, T0));
    }

    [Fact]
    public void State_cannot_be_set_from_outside()
    {
        Assert.All(typeof(WorkflowVersion).GetProperties(), p => Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic, $"{p.Name} has a public setter"));
        Assert.Empty(typeof(WorkflowVersion).GetConstructors());
    }
}
