using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class NameNormalizationTests
{
    [Theory]
    [InlineData("Warehouse", "warehouse")]
    [InlineData("  Warehouse  ", "warehouse")]
    [InlineData("Main   Warehouse", "main warehouse")]
    [InlineData("MAIN WAREHOUSE", "main warehouse")]
    public void Normalize_folds_case_and_collapses_whitespace(string input, string expected)
    {
        Assert.Equal(expected, NameNormalization.Normalize(input));
    }

    // Domain vocabulary is explicit that normalization must never stem or singularize: "warehouse"
    // and "warehouses" are deliberately distinct names, not the same normalized value.
    [Fact]
    public void Normalize_does_not_singularize_or_stem()
    {
        Assert.NotEqual(NameNormalization.Normalize("Warehouse"), NameNormalization.Normalize("Warehouses"));
    }
}
