using DemoCrm.Simulation;

namespace DemoCrm.Tests.Simulation;

public class FailureScenarioTests
{
    [Theory]
    [InlineData("jane+fail-1@example.test", FailureScenarioKind.FailBeforeCommit, 1)]
    [InlineData("jane+fail-3@example.test", FailureScenarioKind.FailBeforeCommit, 3)]
    [InlineData("jane+FAIL-2@Example.test", FailureScenarioKind.FailBeforeCommit, 2)]
    [InlineData("jane+commit-fail@example.test", FailureScenarioKind.CommitThenFail, 0)]
    [InlineData("jane+slow@example.test", FailureScenarioKind.Slow, 0)]
    public void Known_plus_tags_select_the_expected_scenario(string email, FailureScenarioKind kind, int failCount)
    {
        var scenario = FailureScenario.Parse(email);

        Assert.Equal(kind, scenario.Kind);
        Assert.Equal(failCount, scenario.FailCount);
    }

    [Theory]
    [InlineData("jane@example.test")]
    [InlineData("jane+newsletter@example.test")]
    [InlineData("jane+fail-0@example.test")]
    [InlineData("jane+fail-6@example.test")]
    [InlineData("jane+fail-x@example.test")]
    [InlineData("jane+fail-@example.test")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_not_a_failure_scenario(string? email)
    {
        Assert.Equal(FailureScenarioKind.None, FailureScenario.Parse(email).Kind);
    }

    [Fact]
    public void Simulator_counts_attempts_per_address_case_insensitively_and_resets()
    {
        var simulator = new FailureSimulator();

        Assert.Equal(1, simulator.RecordAttempt("a+fail-1@example.test"));
        Assert.Equal(2, simulator.RecordAttempt("A+FAIL-1@example.test"));
        Assert.Equal(1, simulator.RecordAttempt("b+fail-1@example.test"));
        Assert.Equal(2, simulator.TrackedAddresses);

        simulator.Reset();

        Assert.Equal(0, simulator.TrackedAddresses);
        Assert.Equal(1, simulator.RecordAttempt("a+fail-1@example.test"));
    }
}
