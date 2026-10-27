using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Startup validation helpers for <see cref="ImportFetchOptions"/> (M4-01).
/// </summary>
public sealed class ImportFetchOptionsTests
{
    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("application/xhtml+xml")]
    [InlineData("image/webp")]
    public void HasValidContentTypes_accepts_valid_media_types(string value) =>
        ImportFetchOptions.HasValidContentTypes([value]).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a media type")]
    [InlineData("text/")]
    public void HasValidContentTypes_rejects_invalid_media_types(string value) =>
        ImportFetchOptions.HasValidContentTypes([value]).Should().BeFalse();

    [Fact]
    public void HasValidContentTypes_rejects_null_entries() =>
        ImportFetchOptions.HasValidContentTypes(["text/html", null!]).Should().BeFalse();

    [Fact]
    public void HasValidContentTypes_rejects_null_or_empty_lists()
    {
        ImportFetchOptions.HasValidContentTypes(null).Should().BeFalse();
        ImportFetchOptions.HasValidContentTypes([]).Should().BeFalse();
    }
}
