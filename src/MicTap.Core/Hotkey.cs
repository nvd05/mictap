namespace MicTap.Core;

/// <summary>Modifier flags with the values of Win32 RegisterHotKey (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global hotkey parsed from settings.json, such as "Ctrl+Alt+L"; <see cref="VirtualKey"/> is a Win32 VK code.</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey, string Text)
{
    private const int VkSpace = 0x20;

    /// <summary>
    /// Parses "Ctrl+Alt+L": modifiers Ctrl, Alt, Shift, Win and one key — a letter, a digit or F1..F24.
    /// At least one modifier is required. Keyboard layout switches (Win+Space) are refused.
    /// </summary>
    public static bool TryParse(string text, out Hotkey? hotkey, out string error)
    {
        hotkey = null;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (string part in parts)
        {
            HotkeyModifiers modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null || ParseKey(part) is not { } parsed)
            {
                error = $"«{text}»: непонятная клавиша «{part}»; нужен вид Ctrl+Alt+L.";
                return false;
            }

            key = parsed;
        }

        if (key is null || modifiers == HotkeyModifiers.None)
        {
            error = $"«{text}»: нужны модификатор (Ctrl, Alt, Shift, Win) и клавиша, например Ctrl+Alt+L.";
            return false;
        }

        if (key == VkSpace && modifiers == HotkeyModifiers.Win)
        {
            error = $"«{text}» переключает раскладку Windows, выберите другое сочетание.";
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value, text);
        error = "";
        return true;
    }

    private static int? ParseKey(string key)
    {
        string upper = key.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            return upper[0]; // VK codes of letters and digits are their ASCII codes
        }

        if (upper == "SPACE")
        {
            return VkSpace;
        }

        if (upper.Length > 1 && upper[0] == 'F' && int.TryParse(upper[1..], out int number) && number is >= 1 and <= 24)
        {
            return 0x70 + number - 1; // VK_F1
        }

        return null;
    }
}
