using AutomataFlow.Automation.Browser;
using AutomataFlow.Automation.Network.Proxy;

namespace AutomataFlow.UnitTests.Automation;

/// <summary>Phase 4 step 2: operation options, upload/row models and option validation (docs/PHASE4_PLAN.md §0, §1.6).</summary>
public class Step2ModelAndOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(600_001)]
    public void Operation_timeouts_must_be_positive_and_bounded(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationOptions(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Operation_options_without_timeout_keep_the_defaults()
    {
        Assert.Null(new OperationOptions().Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), OperationOptions.WithTimeout(TimeSpan.FromSeconds(5)).Timeout);
    }

    [Fact]
    public void An_explicit_budget_is_one_total_deadline_shared_by_sub_steps()
    {
        var budget = new OperationBudget(TimeSpan.FromMilliseconds(200), isExplicit: true);

        var first = budget.NextMs(TimeSpan.FromSeconds(30));
        Thread.Sleep(250);

        Assert.True(first <= 200, $"first sub-step got {first} ms");
        Assert.Throws<TimeoutException>(() => budget.NextMs(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_default_budget_keeps_each_sub_step_legacy_timeout()
    {
        var budget = new OperationBudget(TimeSpan.FromMilliseconds(1), isExplicit: false);
        Thread.Sleep(5);

        Assert.Equal(10_000f, budget.NextMs(TimeSpan.FromSeconds(10)));
        Assert.Equal(30_000f, budget.NextMs(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Upload_files_are_content_only_and_print_without_their_path()
    {
        var file = new UploadFile("../../etc/report.csv", "text/csv", new byte[] { 1, 2, 3 });

        Assert.Equal("[report.csv, 3 bytes]", file.ToString());
        Assert.Throws<ArgumentException>(() => new UploadFile(" ", "text/csv", Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new UploadFile("a.csv", "", Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("data-key")]
    [InlineData("data_id")]
    [InlineData("aria:label")]
    public void Row_attribute_specs_accept_plain_attribute_names(string name)
    {
        Assert.Equal(RowValueKind.Attribute, RowValueSpec.OfAttribute(name).Kind);
    }

    [Theory]
    [InlineData("data key")]
    [InlineData("x\"y")]
    [InlineData("on<click>")]
    public void Row_attribute_specs_reject_anything_that_is_not_a_plain_attribute_name(string name)
    {
        Assert.Throws<ArgumentException>(() => RowValueSpec.OfAttribute(name));
    }

    /// <summary>D3: XPath can leave the row (parenthesised expressions, unions, parent/ancestor axes), so it is rejected for row children.</summary>
    [Theory]
    [InlineData("//td")]
    [InlineData("(//td)[1]")]
    [InlineData("(//td | //p)")]
    [InlineData("/html/body//td")]
    public void Row_child_selectors_reject_xpath_because_it_can_escape_the_row(string xpath)
    {
        Assert.Throws<ArgumentException>(() => RowValueSpec.OfChildText(ElementSelector.XPath(xpath)));
    }

    [Fact]
    public void Row_child_selectors_accept_descendant_scoped_kinds()
    {
        Assert.Equal(RowValueKind.ChildText, RowValueSpec.OfChildText(ElementSelector.Css("td.email")).Kind);
        Assert.Equal(RowValueKind.ChildText, RowValueSpec.OfChildText(ElementSelector.TestId("email")).Kind);
        Assert.Equal(RowValueKind.ChildText, RowValueSpec.OfChildText(ElementSelector.Text("x")).Kind);
    }

    [Fact]
    public void A_row_snapshot_with_a_count_but_no_values_is_truncated()
    {
        Assert.True(new RowSnapshot(60, []).Truncated);
        Assert.False(new RowSnapshot(0, []).Truncated);
        Assert.False(new RowSnapshot(1, [["a"]]).Truncated);
    }

    [Fact]
    public void Development_private_network_origins_are_rejected_outside_Development()
    {
        var options = new BrowserEngineOptions { Mode = ExecutionMode.SelfHosted, DevelopmentPrivateNetworkOrigins = ["http://127.0.0.1:5080"] };

        var result = new BrowserEngineOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("only be configured in Development", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://*.example.test")]
    [InlineData("http://127.0.0.1:5080/path")]
    [InlineData("127.0.0.1:5080")]
    [InlineData("http://user@127.0.0.1:5080")]
    public void Development_private_network_origins_must_be_exact_origins(string origin)
    {
        var options = new BrowserEngineOptions { Mode = ExecutionMode.Development, DevelopmentPrivateNetworkOrigins = [origin] };

        Assert.True(new BrowserEngineOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void A_canonical_development_origin_is_accepted_in_Development()
    {
        var options = new BrowserEngineOptions { Mode = ExecutionMode.Development, DevelopmentPrivateNetworkOrigins = ["http://127.0.0.1:5080"] };

        Assert.True(new BrowserEngineOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100L * 1024 * 1024 + 1)]
    public void Upload_size_limits_are_bounded(long maxBytes)
    {
        var options = new BrowserEngineOptions { Uploads = new UploadLimits { MaxBytes = maxBytes } };

        Assert.True(new BrowserEngineOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(61, 30)]
    [InlineData(10, 4)]
    [InlineData(10, 121)]
    public void Egress_revocation_and_fail_fast_options_are_bounded(int revocationSeconds, int userTimeoutSeconds)
    {
        var options = new EgressProxyOptions { RevocationTimeoutSeconds = revocationSeconds, UpstreamUserTimeoutSeconds = userTimeoutSeconds };

        Assert.True(new EgressProxyOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Default_egress_options_are_valid()
    {
        Assert.True(new EgressProxyOptionsValidator().Validate(null, new EgressProxyOptions()).Succeeded);
    }
}
