using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// Glyph fallback for the bundled feed faces (Beautiful Feed PR2). Geist and
/// Monaspace are Latin/technical families, so arrows, box-drawing rules, block
/// elements and CJK would otherwise fall through to whatever the platform
/// happens to offer (or render as tofu). Fallbacks are consulted only for
/// glyphs the requested face lacks, in order:
/// 1. Segoe UI Symbol - arrows, geometric shapes, box drawing, block elements;
/// 2. Microsoft YaHei - CJK unified ideographs and kana;
/// 3. Malgun Gothic - hangul.
/// Data only: Program.cs registers it with the font manager, tests assert the
/// resolution, and screenshots show the rendered glyphs. Chrome (Geist) and
/// the feed faces share the chain, so one list keeps the whole app consistent.
/// </summary>
public static class AxFontFallback
{
    public static FontManagerOptions Options { get; } = new()
    {
        FontFallbacks =
        [
            new FontFallback { FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI") },
            new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei") },
            new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
        ],
    };
}
