namespace MicTap.Core.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+L", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4C)]
    [InlineData("ctrl + alt + r", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52)]
    [InlineData("Win+Shift+7", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x37)]
    [InlineData("Control+F5", HotkeyModifiers.Control, 0x74)]
    [InlineData("Alt+F24", HotkeyModifiers.Alt, 0x87)]
    [InlineData("Ctrl+Space", HotkeyModifiers.Control, 0x20)]
    public void Valid_hotkeys_parse_to_modifiers_and_virtual_key(string text, HotkeyModifiers modifiers, int virtualKey)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey? hotkey, out string error), error);

        Assert.Equal(modifiers, hotkey!.Modifiers);
        Assert.Equal(virtualKey, hotkey.VirtualKey);
        Assert.Equal(text, hotkey.Text);
    }

    [Theory]
    [InlineData("L")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Alt+Shift")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Alt+Foo")]
    [InlineData("Ctrl+L+K")]
    [InlineData("Ctrl+F25")]
    [InlineData("Win+Space")]
    [InlineData("")]
    public void Invalid_or_layout_switch_hotkeys_are_refused_with_a_reason(string text)
    {
        Assert.False(Hotkey.TryParse(text, out Hotkey? hotkey, out string error));

        Assert.Null(hotkey);
        Assert.NotEmpty(error);
    }
}
