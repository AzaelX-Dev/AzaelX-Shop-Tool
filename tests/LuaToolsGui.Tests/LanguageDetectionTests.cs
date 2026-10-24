using System.Globalization;
using LuaToolsGui;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The integrated GUI ships only English and Thai resources. Thai OS cultures should select Thai;
/// every other OS culture should safely fall back to English until that language is actually shipped.
/// This keeps automatic language detection consistent with Program.SupportedLanguages and the Settings UI.
/// </summary>
public class LanguageDetectionTests
{
    private static string Match(string osCultureName) =>
        Program.MatchOsLanguage(new CultureInfo(osCultureName));

    [Theory]
    [InlineData("th", "th")]
    [InlineData("th-TH", "th")]
    public void ThaiCultures_SelectThai(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("en-AU", "en")]
    public void EnglishCultures_SelectEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("zh-CN")]
    [InlineData("pt-BR")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    [InlineData("ru-RU")]
    [InlineData("es")]
    [InlineData("")]
    public void UnsupportedOrInvariantCultures_FallBackToEnglish(string os) =>
        Assert.Equal("en", Match(os));
}
