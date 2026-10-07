namespace BossMod;

public enum Language
{
    [PropertyDisplay("Default (English)")]
    English,

    [PropertyDisplay("日本語")]
    Japanese,
}

public static class Loc
{
    public static Language Current = Language.English;

    public static string Tr(string english)
        => Current == Language.Japanese && LocalizationJapanese.Map.TryGetValue(english, out var translated)
            ? translated
            : english;

    // Same, for a text with {0}, {1}... placeholders; the placeholders are filled after the translation
    public static string Trf(string english, params object?[] args) => string.Format(Tr(english), args);

    // Track and option names of the rotation modules: the strategy texts first, then the general ones.
    public static string TrStrategy(string english)
        => Current == Language.Japanese && LocalizationJapaneseStrategy.Map.TryGetValue(english, out var translated)
            ? translated
            : Tr(english);
}
