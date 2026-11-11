using AutomataFlow.Application.Validation;

namespace AutomataFlow.UnitTests.Application;

public class ValidationResultTests
{
    [Fact]
    public void New_result_is_valid()
    {
        var result = new ValidationResult();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Adding_an_error_makes_result_invalid_and_keeps_details()
    {
        var result = new ValidationResult().Add("steps[0].config.selector", "required", "Selector is required.");

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("steps[0].config.selector", error.Path);
        Assert.Equal("required", error.Code);
        Assert.Equal("Selector is required.", error.Message);
    }

    [Fact]
    public void Merge_combines_errors_in_order()
    {
        var first = new ValidationResult().Add("a", "c1", "m1");
        var second = new ValidationResult().Add("b", "c2", "m2");

        first.Merge(second);

        Assert.Equal(["a", "b"], first.Errors.Select(e => e.Path));
    }
}
