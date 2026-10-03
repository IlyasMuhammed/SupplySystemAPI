using FluentAssertions;
using SMS.Modules.Integration.Auth;

namespace SMS.Modules.Integration.Tests.Auth;

public class ApiKeyGeneratorTests
{
    [Fact]
    public void A_key_is_the_marker_plus_forty_url_safe_characters()
    {
        var key = ApiKeyGenerator.NewKey();

        key.Should().StartWith("sqb_").And.HaveLength(44).And.MatchRegex("^sqb_[A-Za-z0-9_-]{40}$");
        ApiKeyGenerator.IsWellFormed(key).Should().BeTrue();
    }

    [Fact]
    public void Keys_do_not_repeat()
    {
        Enumerable.Range(0, 1000).Select(_ => ApiKeyGenerator.NewKey()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_prefix_is_the_first_twelve_characters()
    {
        var key = ApiKeyGenerator.NewKey();

        ApiKeyGenerator.PrefixOf(key).Should().Be(key[..12]).And.StartWith("sqb_");
    }

    [Fact]
    public void The_hash_is_lower_case_sha256_hex_and_matches_only_its_key()
    {
        var key  = ApiKeyGenerator.NewKey();
        var hash = ApiKeyGenerator.Hash(key);

        hash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$").And.NotContain(key[4..]);
        ApiKeyGenerator.Matches(key, hash).Should().BeTrue();
        ApiKeyGenerator.Matches(key, hash.ToUpperInvariant()).Should().BeTrue("the stored case does not matter");
        ApiKeyGenerator.Matches(ApiKeyGenerator.NewKey(), hash).Should().BeFalse();
        ApiKeyGenerator.Matches(key, "abc").Should().BeFalse("a truncated hash never matches");
    }

    public static IEnumerable<object?[]> Malformed()
    {
        var body = new string('A', 39);
        yield return [null];
        yield return [""];
        yield return ["sqb_"];
        yield return ["sqb_" + body];                  // 39 characters
        yield return ["sqb_" + body + "AA"];           // 41
        yield return ["SQB_" + body + "A"];            // wrong marker
        yield return ["key_" + body + "A"];
        yield return ["sqb_" + body + "="];            // padding
        yield return ["sqb_" + body + "/"];            // not URL-safe
        yield return ["sqb_" + body + "+"];
        yield return ["sqb_" + body[..20] + " " + body[..19]];
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Anything_else_is_malformed(string? key)
    {
        ApiKeyGenerator.IsWellFormed(key).Should().BeFalse();
    }

    [Fact]
    public void The_canonical_shape_is_well_formed()
    {
        ApiKeyGenerator.IsWellFormed("sqb_" + new string('A', 20) + new string('z', 10) + "0123456-_9").Should().BeTrue();
    }
}
