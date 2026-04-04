using System.Globalization;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace GenericModDocumentationFramework
{
    public class ModConfig
    {
        // ── Controls ────────────────────────────────────────────────────────────
        public KeybindList OpenMenuKey   { get; set; } = KeybindList.Parse("F2");
        public bool        ShowHudButton { get; set; } = true;

        // ── Theme ────────────────────────────────────────────────────────────────

        /// <summary>Background fill of selected sidebar item and selected page tab.</summary>
        public string AccentColor { get; set; } = "B14E05";           // (177, 78, 5)

        /// <summary>Color of the thick border around the documentation content pane.</summary>
        public string ContentBorderColor { get; set; } = "B48C50";    // (180, 140, 80)

        /// <summary>Color of the scrollbar thumb.</summary>
        public string ScrollBarColor { get; set; } = "B48C50";        // (180, 140, 80)
    }

    /// <summary>Helpers for converting between hex strings and XNA Color values.</summary>
    public static class ColorHelper
    {
        /// <summary>
        /// Parses a 6-digit hex string (with or without leading #) to a Color.
        /// Returns <paramref name="fallback"/> if parsing fails.
        /// </summary>
        public static Color Parse(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            hex = hex.TrimStart('#').Trim();
            if (hex.Length != 6) return fallback;
            try
            {
                byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                return new Color(r, g, b, (byte)255);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Serialises a Color to an uppercase 6-digit hex string.</summary>
        public static string ToHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
