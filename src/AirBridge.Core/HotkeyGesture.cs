namespace AirBridge.Core;

public readonly record struct HotkeyGesture(bool Ctrl, bool Alt, bool Shift, bool Win, int VirtualKey)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static readonly IReadOnlyDictionary<string, int> NamedKeys = CreateNamedKeys();
    private static readonly IReadOnlyDictionary<int, string> CanonicalKeyNames = CreateCanonicalKeyNames();

    public static HotkeyGesture Default => new(true, true, false, false, 0x20);
    public bool IsValid => VirtualKey is > 0 and <= 0xFF && (Ctrl || Alt || Shift || Win || VirtualKey is >= 0x7C and <= 0x87);

    public static bool TryParse(string? value, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        int? virtualKey = null;
        var tokens = value.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Any(string.IsNullOrWhiteSpace)) return false;
        foreach (var rawToken in tokens)
        {
            if (rawToken.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || rawToken.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                if (ctrl) return false;
                ctrl = true;
            }
            else if (rawToken.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                if (alt) return false;
                alt = true;
            }
            else if (rawToken.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                if (shift) return false;
                shift = true;
            }
            else if (rawToken.Equals("Win", StringComparison.OrdinalIgnoreCase) || rawToken.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                if (win) return false;
                win = true;
            }
            else
            {
                if (virtualKey is not null || !TryParseKey(rawToken, out var parsedKey)) return false;
                virtualKey = parsedKey;
            }
        }

        if (virtualKey is null) return false;
        gesture = new(ctrl, alt, shift, win, virtualKey.Value);
        return gesture.IsValid;
    }

    public (uint modifiers, uint vk) ToRegisterHotKeyArgs()
    {
        var modifiers = ModNoRepeat;
        if (Ctrl) modifiers |= ModControl;
        if (Alt) modifiers |= ModAlt;
        if (Shift) modifiers |= ModShift;
        if (Win) modifiers |= ModWin;
        return (modifiers, (uint)VirtualKey);
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(FormatKey(VirtualKey));
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string token, out int virtualKey)
    {
        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        if (token.Length == 2 && token[0] is 'D' or 'd' && char.IsDigit(token[1]))
        {
            virtualKey = token[1];
            return true;
        }

        if (token.Length is 2 or 3 && (token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), out var function) && function is >= 1 and <= 24)
        {
            virtualKey = 0x70 + function - 1;
            return true;
        }

        return NamedKeys.TryGetValue(token, out virtualKey);
    }

    private static string FormatKey(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x30 and <= 0x39) return $"D{(char)virtualKey}";
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x70 + 1}";
        return CanonicalKeyNames.TryGetValue(virtualKey, out var name) ? name : $"0x{virtualKey:X2}";
    }

    private static IReadOnlyDictionary<string, int> CreateNamedKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Back"] = 0x08, ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D,
            ["LineFeed"] = 0x0A, ["Clear"] = 0x0C,
            ["Pause"] = 0x13, ["CapsLock"] = 0x14, ["Capital"] = 0x14, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
            ["HangulMode"] = 0x15, ["HanguelMode"] = 0x15, ["KanaMode"] = 0x15, ["JunjaMode"] = 0x17,
            ["FinalMode"] = 0x18, ["HanjaMode"] = 0x19, ["KanjiMode"] = 0x19, ["IMEConvert"] = 0x1C,
            ["IMENonconvert"] = 0x1D, ["IMEAccept"] = 0x1E, ["IMEAceept"] = 0x1E, ["IMEModeChange"] = 0x1F,
            ["Space"] = 0x20, ["PageUp"] = 0x21, ["Prior"] = 0x21, ["PageDown"] = 0x22, ["Next"] = 0x22,
            ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
            ["Select"] = 0x29, ["Print"] = 0x2A, ["Execute"] = 0x2B, ["PrintScreen"] = 0x2C, ["Snapshot"] = 0x2C,
            ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Help"] = 0x2F,
            ["Apps"] = 0x5D, ["Sleep"] = 0x5F, ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Separator"] = 0x6C,
            ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F, ["NumLock"] = 0x90, ["Scroll"] = 0x91,
            ["BrowserBack"] = 0xA6, ["BrowserForward"] = 0xA7, ["BrowserRefresh"] = 0xA8, ["BrowserStop"] = 0xA9,
            ["BrowserSearch"] = 0xAA, ["BrowserFavorites"] = 0xAB, ["BrowserHome"] = 0xAC, ["VolumeMute"] = 0xAD,
            ["VolumeDown"] = 0xAE, ["VolumeUp"] = 0xAF, ["MediaNextTrack"] = 0xB0, ["MediaPreviousTrack"] = 0xB1,
            ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3, ["LaunchMail"] = 0xB4, ["SelectMedia"] = 0xB5,
            ["LaunchApplication1"] = 0xB6, ["LaunchApplication2"] = 0xB7, ["Oem1"] = 0xBA, ["OemSemicolon"] = 0xBA,
            ["Oemplus"] = 0xBB, ["Oemcomma"] = 0xBC, ["OemMinus"] = 0xBD, ["OemPeriod"] = 0xBE,
            ["OemQuestion"] = 0xBF, ["Oem2"] = 0xBF, ["Oem3"] = 0xC0, ["Oemtilde"] = 0xC0,
            ["OemOpenBrackets"] = 0xDB, ["Oem4"] = 0xDB, ["Oem5"] = 0xDC, ["OemPipe"] = 0xDC,
            ["OemCloseBrackets"] = 0xDD, ["Oem6"] = 0xDD, ["Oem7"] = 0xDE, ["OemQuotes"] = 0xDE,
            ["Oem8"] = 0xDF, ["OemBackslash"] = 0xE2, ["Oem102"] = 0xE2, ["ProcessKey"] = 0xE5,
            ["Packet"] = 0xE7, ["Attn"] = 0xF6, ["Crsel"] = 0xF7, ["Exsel"] = 0xF8, ["EraseEof"] = 0xF9,
            ["Play"] = 0xFA, ["Zoom"] = 0xFB, ["NoName"] = 0xFC, ["Pa1"] = 0xFD, ["OemClear"] = 0xFE
        };
        for (var index = 0; index <= 9; index++) keys[$"NumPad{index}"] = 0x60 + index;
        return keys;
    }

    private static IReadOnlyDictionary<int, string> CreateCanonicalKeyNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var pair in NamedKeys) names.TryAdd(pair.Value, pair.Key);
        names[0x08] = "Back";
        names[0x0D] = "Enter";
        names[0x14] = "CapsLock";
        names[0x1B] = "Esc";
        names[0x21] = "PageUp";
        names[0x22] = "PageDown";
        names[0x2C] = "PrintScreen";
        return names;
    }
}
