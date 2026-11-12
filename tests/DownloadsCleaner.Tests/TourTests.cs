using System.Windows;
using DownloadsCleaner.Controls;
using DownloadsCleaner.Localization;

namespace DownloadsCleaner.Tests;

/// <summary>Тур по программе: тексты, когда показывать и где ставить карточку.</summary>
public class TourTests
{
    [Fact]
    public void Every_step_has_title_and_text_in_both_languages()
    {
        foreach (var key in TourContent.StepKeys)
            foreach (var suffix in new[] { "Title", "Text" })
            {
                var k = $"Tour{key}{suffix}";
                Assert.True(Loc.Russian.ContainsKey(k), $"нет русского текста {k}");
                Assert.True(Loc.English.ContainsKey(k), $"нет английского текста {k}");
            }
    }

    [Theory]
    [InlineData(null, "1.0.0", true)]       // первый запуск
    [InlineData("", "1.0.0", true)]
    [InlineData("1.0.0", "1.1.0", true)]    // после обновления
    [InlineData("1.1.0", "1.1.0", false)]   // уже пройден для этой версии
    public void Tour_shows_on_first_start_and_after_update_only(string? last, string current, bool expected)
    {
        Assert.Equal(expected, TourContent.ShouldShow(last, Version.Parse(current)));
    }

    private static readonly Size Area = new(1360, 860);
    private static readonly Size Card = new(380, 220);

    private static void AssertInside(Point p)
    {
        Assert.InRange(p.X, 0, Area.Width - Card.Width);
        Assert.InRange(p.Y, 0, Area.Height - Card.Height);
    }

    [Fact]
    public void Card_goes_right_of_the_sidebar()
    {
        var navItem = new Rect(12, 300, 228, 44);
        var p = TourContent.PlaceCard(navItem, Card, Area);
        Assert.True(p.X >= navItem.Right);
        AssertInside(p);
    }

    [Fact]
    public void Card_goes_under_a_header_button()
    {
        var button = new Rect(1000, 11, 120, 34);
        var p = TourContent.PlaceCard(button, Card, Area);
        Assert.True(p.Y >= button.Bottom);
        AssertInside(p);
    }

    [Fact]
    public void Card_stays_inside_a_large_target_and_centers_without_target()
    {
        var page = new Rect(252, 56, 1108, 804);
        AssertInside(TourContent.PlaceCard(page, Card, Area));

        var center = TourContent.PlaceCard(null, Card, Area);
        Assert.Equal((Area.Width - Card.Width) / 2, center.X, 3);
        Assert.Equal((Area.Height - Card.Height) / 2, center.Y, 3);
    }

    [Fact]
    public void Card_near_the_bottom_edge_is_kept_on_screen()
    {
        var lowItem = new Rect(12, 820, 228, 36);
        AssertInside(TourContent.PlaceCard(lowItem, Card, Area));
    }
}
