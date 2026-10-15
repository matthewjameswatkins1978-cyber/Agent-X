using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// Beautiful Feed PR2 glyph fallback is data (Program.cs registers it with
/// the font manager): the bundled Geist/Monaspace faces are Latin, so the
/// chain must offer symbols first (arrows, box drawing, block elements),
/// then CJK ideographs and kana, then hangul. Actual glyph resolution and
/// the rendered CJK/arrow pixels are evidenced by the running-app
/// screenshots, per the repo's no-headless-rendering convention.
/// </summary>
public sealed class AxFontFallbackTests
{
    [Fact]
    public void Chain_IsOrderedSymbolsThenCjkThenHangul()
    {
        var families = AxFontFallback.Options.FontFallbacks!
            .Select(f => f.FontFamily.Name)
            .ToArray();

        Assert.Equal(3, families.Length);
        Assert.Contains("Segoe UI Symbol", families[0], StringComparison.Ordinal);
        Assert.Contains("Microsoft YaHei", families[1], StringComparison.Ordinal);
        Assert.Contains("Malgun Gothic", families[2], StringComparison.Ordinal);
        // The symbol and CJK entries are stacks (a system without the primary
        // family still has a candidate), not single names.
        Assert.All(AxFontFallback.Options.FontFallbacks!.Take(2),
            f => Assert.True(f.FontFamily.FamilyNames.Count >= 2, $"{f.FontFamily.Name} should be a font stack"));
    }
}
