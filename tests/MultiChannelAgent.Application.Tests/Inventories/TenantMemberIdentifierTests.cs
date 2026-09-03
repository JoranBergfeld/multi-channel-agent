using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class TenantMemberIdentifierTests
{
    [Fact]
    public void Parse_recognizes_an_exact_guid_as_an_object_id()
    {
        var guid = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var identifier = TenantMemberIdentifier.Parse(guid.ToString());

        Assert.NotNull(identifier);
        Assert.Equal(guid, identifier!.ObjectId);
        Assert.Null(identifier.Address);
    }

    [Fact]
    public void Parse_recognizes_an_address_containing_at_sign_as_a_tenant_address()
    {
        var identifier = TenantMemberIdentifier.Parse("ada@example.com");

        Assert.NotNull(identifier);
        Assert.Null(identifier!.ObjectId);
        Assert.Equal("ada@example.com", identifier.Address);
    }

    [Fact]
    public void Parse_trims_whitespace_around_an_address()
    {
        var identifier = TenantMemberIdentifier.Parse("  ada@example.com  ");

        Assert.Equal("ada@example.com", identifier!.Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid-or-address")]
    public void Parse_rejects_anything_that_is_neither_an_exact_guid_nor_an_address(string? raw)
    {
        Assert.Null(TenantMemberIdentifier.Parse(raw));
    }

    // "Never search/guess fuzzy people": a near-miss GUID (one character off) must never be coerced
    // into a valid identifier.
    [Fact]
    public void Parse_rejects_a_malformed_guid_rather_than_guessing()
    {
        Assert.Null(TenantMemberIdentifier.Parse("11111111-1111-1111-1111-11111111111"));
    }
}
