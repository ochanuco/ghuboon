using Ghuboon.Infrastructure.Notifications;

namespace Ghuboon.Tests.Infrastructure.Notifications;

public class MacOSDesktopNotificationServiceTests
{
    [Fact]
    public void EscapeForAppleScript_passes_through_plain_text()
    {
        Assert.Equal("hello world", MacOSDesktopNotificationService.EscapeForAppleScript("hello world"));
    }

    [Fact]
    public void EscapeForAppleScript_escapes_double_quotes()
    {
        var input = "say \"hello\"";
        var output = MacOSDesktopNotificationService.EscapeForAppleScript(input);
        Assert.Equal("say \\\"hello\\\"", output);
    }

    [Fact]
    public void EscapeForAppleScript_escapes_backslashes_before_quotes()
    {
        // Backslashes must be doubled first so quote-escapes don't get
        // re-escaped on the second pass.
        var input = "C:\\path\\\"with quotes\"";
        var output = MacOSDesktopNotificationService.EscapeForAppleScript(input);
        Assert.Equal("C:\\\\path\\\\\\\"with quotes\\\"", output);
    }

    [Fact]
    public void EscapeForAppleScript_handles_empty_and_null()
    {
        Assert.Equal(string.Empty, MacOSDesktopNotificationService.EscapeForAppleScript(string.Empty));
        Assert.Equal(string.Empty, MacOSDesktopNotificationService.EscapeForAppleScript(null!));
    }

    [Fact]
    public void BuildAppleScript_wraps_title_and_body_in_display_notification_command_with_sound()
    {
        var script = MacOSDesktopNotificationService.BuildAppleScript("Mention: x/y", "PR title");
        Assert.Equal(
            "display notification \"PR title\" with title \"Mention: x/y\" sound name \"Glass\"",
            script);
    }

    [Fact]
    public void BuildAppleScript_escapes_special_characters_in_title_and_body()
    {
        var script = MacOSDesktopNotificationService.BuildAppleScript(
            "title with \"quote\"",
            "body with \\backslash and \"quote\"");

        Assert.Equal(
            "display notification \"body with \\\\backslash and \\\"quote\\\"\" with title \"title with \\\"quote\\\"\" sound name \"Glass\"",
            script);
    }

    [Fact]
    public void BuildAppleScript_escapes_sound_name()
    {
        var script = MacOSDesktopNotificationService.BuildAppleScript("t", "b", "Custom\"Sound");
        Assert.Contains("sound name \"Custom\\\"Sound\"", script);
    }
}
