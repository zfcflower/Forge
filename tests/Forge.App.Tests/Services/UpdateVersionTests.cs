using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins <see cref="UpdateVersion"/> (wave W10b): the CI <c>0.0.0-ci.*</c> marker is a development build, the numeric fields
/// decide first, and the pre-release identifiers follow SemVer precedence (a newer beta of the same triplet is an update, a
/// pre-release is older than its release, build metadata is ignored).
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("0.0.0-ci")]
    [InlineData("0.0.0-ci.123")]
    [InlineData("0.0.0-ci.999999")]
    [InlineData("0.0.0-ci.")]
    public void The_ci_stamp_is_a_development_build(string version)
    {
        Assert.True(UpdateVersion.IsDevBuild(version));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.2.0")]
    [InlineData("1.2.0-beta.1")]
    [InlineData("0.0.0")]
    [InlineData("0.0.0-ciX")]
    [InlineData("")]
    public void A_tag_build_or_anything_else_is_not_a_development_build(string version)
    {
        Assert.False(UpdateVersion.IsDevBuild(version));
    }

    [Theory]
    [InlineData("1.0.0", "1.2.0", 1)]
    [InlineData("1.2.0", "1.1.0", -1)]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("1.2.0", "1.2.1", 1)]
    [InlineData("1.2.0.1", "1.2.0", -1)]
    [InlineData("1.2.0", "1.2.0+build.7", 0)]
    [InlineData("1.2.0+build.7", "1.2.0", 0)]
    [InlineData("1.2.0", "1.3.0-beta.1", 1)]
    public void Numeric_fields_decide_before_any_prerelease(string current, string offered, int expectedSign)
    {
        var comparison = UpdateVersion.Compare(current, offered);
        Assert.NotNull(comparison);
        Assert.Equal(expectedSign, Math.Sign(comparison!.Value));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0-beta.1", -1)]
    [InlineData("1.2.0-beta.1", "1.2.0", 1)]
    [InlineData("1.2.0-beta.1+build.7", "1.2.0", 1)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", 1)]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.2", -1)]
    [InlineData("1.2.0-beta", "1.2.0-beta.1", 1)]
    [InlineData("1.2.0-beta.1", "1.2.0-beta", -1)]
    [InlineData("1.2.0-alpha", "1.2.0-beta", 1)]
    [InlineData("1.2.0-beta", "1.2.0-alpha", -1)]
    [InlineData("1.2.0-1", "1.2.0-alpha", 1)]
    [InlineData("1.2.0-alpha", "1.2.0-1", -1)]
    [InlineData("1.2.0-beta.1", "1.2.0-beta.1", 0)]
    public void Prerelease_identifiers_follow_semver_precedence(string current, string offered, int expectedSign)
    {
        // DOCUMENTED DEFECT D2 (see the W10b test report): comparing only the numeric triplet made every beta of the same
        // 1.3.0 compare equal, so the beta channel could never offer beta.2 to a user on beta.1. "beta.2 < beta.10" is the
        // case that separates a numeric identifier comparison from an ordinal one.
        var comparison = UpdateVersion.Compare(current, offered);
        Assert.NotNull(comparison);
        Assert.Equal(expectedSign, Math.Sign(comparison!.Value));
    }

    [Theory]
    [InlineData("not-a-version", "1.2.0")]
    [InlineData("1.0.0", "not-a-version")]
    [InlineData("", "1.2.0")]
    [InlineData("1.2.0", "")]
    [InlineData("1.2.0", "v1.2.0")]
    [InlineData("1.2.0", "1.2.0.0.0")]
    [InlineData("1.2.0", "1.2.x")]
    public void An_unparseable_version_is_null(string current, string offered)
    {
        // null is the caller's signal for a readable "version could not be read" failure rather than a silent up-to-date.
        Assert.Null(UpdateVersion.Compare(current, offered));
    }
}
