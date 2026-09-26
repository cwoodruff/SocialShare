using System.Text;
using SocialShare.Core.Domain;
using SocialShare.Core.Services;
using SocialShare.Core.Text;

namespace SocialShare.Tests;

public class ImageInspectorTests
{
    [Fact]
    public void A_real_png_reports_its_true_type_and_size()
    {
        var info = ImageInspector.Inspect(TestImages.Png(640, 360));

        Assert.NotNull(info);
        Assert.Equal("image/png", info.ContentType);
        Assert.Equal(640, info.Width);
        Assert.Equal(360, info.Height);
    }

    [Fact]
    public void A_real_jpeg_reports_its_true_type_and_size()
    {
        var info = ImageInspector.Inspect(TestImages.Jpeg(1200, 675));

        Assert.NotNull(info);
        Assert.Equal("image/jpeg", info.ContentType);
        Assert.Equal(1200, info.Width);
        Assert.Equal(675, info.Height);
    }

    [Fact]
    public void A_lossy_webp_reports_its_true_type_and_size()
    {
        var info = ImageInspector.Inspect(TestImages.WebPLossy(300, 200));

        Assert.NotNull(info);
        Assert.Equal("image/webp", info.ContentType);
        Assert.Equal(300, info.Width);
        Assert.Equal(200, info.Height);
    }

    [Fact]
    public void A_file_renamed_to_png_is_still_rejected()
    {
        // This is the whole point: the extension and the browser supplied type are ignored.
        var pretendPng = Encoding.UTF8.GetBytes("#!/bin/sh\necho definitely not an image\n");

        Assert.Null(ImageInspector.Inspect(pretendPng));
    }

    [Fact]
    public void A_gif_is_rejected_because_it_is_not_on_the_accepted_list()
    {
        var gif = "GIF89a"u8.ToArray().Concat(new byte[20]).ToArray();

        Assert.Null(ImageInspector.Inspect(gif));
    }

    [Fact]
    public void Truncated_bytes_do_not_throw()
    {
        Assert.Null(ImageInspector.Inspect([0x89, 0x50, 0x4E]));
        Assert.Null(ImageInspector.Inspect([]));
    }
}

public class RichTextTests
{
    [Fact]
    public void A_bare_url_is_found_with_utf8_byte_offsets()
    {
        var spans = RichText.FindSpans("Go to https://woodruff.dev now");

        var link = Assert.Single(spans);
        Assert.Equal(TextSpanKind.Link, link.Kind);
        Assert.Equal("https://woodruff.dev", link.Text);
        Assert.Equal(6, link.ByteStart);
        Assert.Equal(26, link.ByteEnd);
    }

    [Fact]
    public void Offsets_are_bytes_not_characters()
    {
        // The emoji is four UTF-8 bytes but two UTF-16 units, so a naive index would be wrong.
        var spans = RichText.FindSpans("\U0001F600 https://woodruff.dev");

        var link = Assert.Single(spans);
        Assert.Equal(5, link.ByteStart);
        Assert.Equal(25, link.ByteEnd);
    }

    [Fact]
    public void Trailing_punctuation_is_left_out_of_the_link()
    {
        var spans = RichText.FindSpans("Read https://woodruff.dev/posts.");

        Assert.Equal("https://woodruff.dev/posts", spans.Single().Text);
    }

    [Fact]
    public void A_handle_is_a_mention_and_a_plain_word_is_not()
    {
        var spans = RichText.FindSpans("Hi @woodruff.dev and @nobody");

        var mention = Assert.Single(spans);
        Assert.Equal(TextSpanKind.Mention, mention.Kind);
        Assert.Equal("woodruff.dev", mention.Text);
    }

    [Fact]
    public void An_at_sign_inside_a_url_is_not_treated_as_a_mention()
    {
        var spans = RichText.FindSpans("See https://hachyderm.io/@woody for more");

        Assert.Single(spans);
        Assert.Equal(TextSpanKind.Link, spans[0].Kind);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("hello", 5)]
    [InlineData("café", 4)]
    [InlineData("\U0001F600", 1)]
    [InlineData("\U0001F469‍\U0001F4BB", 1)]
    public void Characters_are_counted_the_way_the_platforms_count_them(string body, int expected)
    {
        Assert.Equal(expected, RichText.CountCharacters(body));
    }
}

public class AppTimeZoneTests
{
    [Fact]
    public void A_local_wall_clock_time_round_trips_through_utc()
    {
        var local = new DateTime(2026, 7, 4, 9, 30, 0);

        var utc = AppTimeZone.ToUtc(local, "America/Detroit");

        // Detroit is four hours behind UTC in July.
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 13, 30, 0, TimeSpan.Zero), utc);
        Assert.Equal(local, AppTimeZone.ToLocal(utc, "America/Detroit"));
    }

    [Fact]
    public void Winter_and_summer_get_different_offsets()
    {
        var winter = AppTimeZone.ToUtc(new DateTime(2026, 1, 15, 9, 0, 0), "America/Detroit");
        var summer = AppTimeZone.ToUtc(new DateTime(2026, 7, 15, 9, 0, 0), "America/Detroit");

        Assert.Equal(14, winter.Hour);
        Assert.Equal(13, summer.Hour);
    }

    [Fact]
    public void A_time_that_does_not_exist_is_pushed_past_the_gap()
    {
        // Clocks jump from 2am to 3am on 8 March 2026 in Detroit, so 2:30am never happens.
        var utc = AppTimeZone.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), "America/Detroit");

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_utc_rather_than_throwing()
    {
        Assert.Equal(TimeZoneInfo.Utc, AppTimeZone.Resolve("Mars/Olympus_Mons"));
    }

    [Fact]
    public void Every_offered_zone_actually_resolves()
    {
        foreach (var zone in AppTimeZone.CommonZones)
        {
            // Resolve swallows an unknown id and hands back UTC, so comparing the id back is
            // what actually proves the zone exists on this machine.
            Assert.Equal(zone, AppTimeZone.Resolve(zone).Id);
        }
    }
}

public class FeatureGateTests
{
    [Fact]
    public void The_free_plan_has_a_scheduling_ceiling_and_pro_does_not()
    {
        Assert.Equal(FeatureGate.FreeScheduledPostLimit, FeatureGate.ScheduledPostLimit(Plan.Free));
        Assert.Null(FeatureGate.ScheduledPostLimit(Plan.Pro));
    }

    [Fact]
    public void Paid_only_features_are_off_on_the_free_plan()
    {
        Assert.False(FeatureGate.IsEnabled(Feature.TeamMembers, Plan.Free));
        Assert.True(FeatureGate.IsEnabled(Feature.TeamMembers, Plan.Pro));
    }
}
