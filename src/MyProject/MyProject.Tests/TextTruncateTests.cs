using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>文字截斷（0.10.44 起整併成 <see cref="TextTruncate"/>，取代各處的 Truncate 寫法）。</summary>
public sealed class TextTruncateTests
{
    [Theory]
    [InlineData(null, 3, null)]
    [InlineData("", 3, "")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 3, "abc")]
    [InlineData("中文字串", 2, "中文")]
    public void Cut_ShouldKeepAtMostMaxLength(string? text, int max, string? expected)
        => Assert.Equal(expected, TextTruncate.Cut(text, max));

    [Theory]
    [InlineData(null, 3, null)]
    [InlineData("", 3, "")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 3, "abc…")]
    [InlineData("中文字串", 2, "中文…")]
    public void WithEllipsis_ShouldAppendAnEllipsisOnlyWhenCut(string? text, int max, string? expected)
        => Assert.Equal(expected, TextTruncate.WithEllipsis(text, max));
}
