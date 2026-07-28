using UnityEngine;

namespace BapbapMods.Manager
{
    /// Colours sampled to sit alongside the game's own menus: deep navy surfaces, blue
    /// accents, yellow for anything selected or emphasised. The first pass used saturated
    /// green rows and pale blue buttons, which looked nothing like the game.
    internal static class Palette
    {
        public static readonly Color PageBackground = new Color(0.043f, 0.055f, 0.094f, 1f);

        public static readonly Color Row          = new Color(0.078f, 0.094f, 0.149f, 1f);
        public static readonly Color RowEnabled   = new Color(0.098f, 0.129f, 0.216f, 1f);
        public static readonly Color RowDisabled  = new Color(0.063f, 0.071f, 0.106f, 1f);

        public static readonly Color Accent       = new Color(0.231f, 0.353f, 0.855f, 1f);
        public static readonly Color AccentHover  = new Color(0.322f, 0.451f, 0.933f, 1f);

        public static readonly Color Highlight    = new Color(1f, 0.847f, 0.2f, 1f);
        public static readonly Color TextPrimary  = new Color(0.898f, 0.922f, 0.973f, 1f);
        public static readonly Color TextMuted    = new Color(0.482f, 0.522f, 0.608f, 1f);
    }
}
