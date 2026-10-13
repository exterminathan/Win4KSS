using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// The .lnk Hotkey field expects "CTRL+ALT+S". Explorer will happily save a bare letter and
/// then either never fire it or hijack that key globally, so a modifier is mandatory.
/// </summary>
public class ShortcutHotkeyTests
{
    [Theory]
    [InlineData(Keys.Control | Keys.Alt | Keys.S, "CTRL+ALT+S")]
    [InlineData(Keys.Control | Keys.Shift | Keys.L, "CTRL+SHIFT+L")]
    [InlineData(Keys.Control | Keys.Alt | Keys.Shift | Keys.F9, "CTRL+ALT+SHIFT+F9")]
    [InlineData(Keys.Alt | Keys.F4, "ALT+F4")]
    public void Modifier_combinations_convert(Keys keys, string expected)
    {
        Assert.Equal(expected, ShortcutManager.ToShortcutHotkey(keys));
    }

    [Theory]
    [InlineData(Keys.S)]
    [InlineData(Keys.F5)]
    [InlineData(Keys.Escape)]
    public void A_bare_key_is_rejected(Keys keys)
    {
        Assert.Null(ShortcutManager.ToShortcutHotkey(keys));
    }

    [Theory]
    [InlineData(Keys.Control)]
    [InlineData(Keys.Alt)]
    [InlineData(Keys.Shift)]
    [InlineData(Keys.None)]
    public void A_modifier_with_no_key_is_rejected(Keys keys)
    {
        // Otherwise holding Ctrl on its own would bind "CTRL+CONTROLKEY".
        Assert.Null(ShortcutManager.ToShortcutHotkey(keys));
    }

    [Fact]
    public void Default_hotkey_is_a_valid_combination()
    {
        Assert.Equal(ShortcutManager.DefaultHotkey,
            ShortcutManager.ToShortcutHotkey(Keys.Control | Keys.Alt | Keys.S));
    }
}
