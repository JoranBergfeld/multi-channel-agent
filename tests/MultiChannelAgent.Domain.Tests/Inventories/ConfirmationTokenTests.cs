using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public sealed class ConfirmationTokenTests
{
    [Fact]
    public void An_issued_token_is_opaque_url_safe_text_of_a_fixed_length()
    {
        var token = ConfirmationToken.Issue();

        Assert.Equal(ConfirmationToken.TextLength, token.Length);
        Assert.True(ConfirmationToken.IsWellFormed(token));
        Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void Two_issued_tokens_are_never_the_same()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => ConfirmationToken.Issue()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_hash_is_fixed_length_lowercase_hexadecimal_and_is_not_the_token()
    {
        var token = ConfirmationToken.Issue();

        var hash = ConfirmationToken.HashOf(token);

        Assert.Equal(ConfirmationToken.HashTextLength, hash.Value.Length);
        Assert.All(hash.Value, c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));
        Assert.DoesNotContain(token, hash.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hashing_the_same_token_twice_gives_the_same_hash()
    {
        var token = ConfirmationToken.Issue();

        Assert.Equal(ConfirmationToken.HashOf(token), ConfirmationToken.HashOf(token));
    }

    [Fact]
    public void A_stored_hash_matches_only_the_token_it_was_made_from()
    {
        var token = ConfirmationToken.Issue();
        var hash = ConfirmationToken.HashOf(token);

        Assert.True(ConfirmationToken.Matches(hash, token));
        Assert.False(ConfirmationToken.Matches(hash, ConfirmationToken.Issue()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void Text_that_is_not_a_well_formed_token_never_matches_anything(string? presented)
    {
        var hash = ConfirmationToken.HashOf(ConfirmationToken.Issue());

        Assert.False(ConfirmationToken.IsWellFormed(presented));
        Assert.False(ConfirmationToken.Matches(hash, presented));
    }

    [Fact]
    public void A_hash_read_back_from_storage_still_matches_its_token()
    {
        var token = ConfirmationToken.Issue();
        var roundTripped = new ConfirmationTokenHash(ConfirmationToken.HashOf(token).Value);

        Assert.True(ConfirmationToken.Matches(roundTripped, token));
    }
}
