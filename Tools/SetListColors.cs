using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace KronosScreenRemote.Tools
{
    public readonly record struct SetListColors : IEquatable<SetListColors>
    {
        public byte Index { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public string DisplayName { get; }

        private SetListColors(byte index, byte r, byte g, byte b, string displayName)
        {
            Index = index;
            R = r;
            G = g;
            B = b;
            DisplayName = displayName;
        }

        // Kronos Set List 16-slot color palette (authentic values from device)
        public static readonly SetListColors Default = new(0, 0x4D, 0x4D, 0x4D, "Default");
        public static readonly SetListColors Charcoal = new(1, 0x2F, 0x2F, 0x2F, "Charcoal");
        public static readonly SetListColors Brick = new(2, 0xB2, 0x3F, 0x3F, "Brick");
        public static readonly SetListColors Burgundy = new(3, 0x69, 0x1B, 0x1B, "Burgundy");
        public static readonly SetListColors Ivy = new(4, 0x91, 0xA7, 0x30, "Ivy");
        public static readonly SetListColors Olive = new(5, 0x37, 0x45, 0x20, "Olive");
        public static readonly SetListColors Gold = new(6, 0xAA, 0x84, 0x2A, "Gold");
        public static readonly SetListColors Cacao = new(7, 0x7F, 0x42, 0x36, "Cacao");
        public static readonly SetListColors Indigo = new(8, 0x53, 0x60, 0xA5, "Indigo");
        public static readonly SetListColors Navy = new(9, 0x1A, 0x2B, 0x88, "Navy");
        public static readonly SetListColors Rose = new(10, 0xAB, 0x81, 0xA2, "Rose");
        public static readonly SetListColors Lavender = new(11, 0x92, 0x67, 0xBA, "Lavender");
        public static readonly SetListColors Azure = new(12, 0x88, 0xA4, 0xC5, "Azure");
        public static readonly SetListColors Denim = new(13, 0x6A, 0x7F, 0x96, "Denim");
        public static readonly SetListColors Silver = new(14, 0x80, 0x80, 0x80, "Silver");
        public static readonly SetListColors Slate = new(15, 0x62, 0x62, 0x62, "Slate");

        /// <summary>
        /// All color palette entries in index order.
        /// </summary>
        private static readonly SetListColors[] AllColors = new[]
        {
            Default, Charcoal, Brick, Burgundy, Ivy, Olive, Gold, Cacao,
            Indigo, Navy, Rose, Lavender, Azure, Denim, Silver, Slate,
        };

        /// <summary>
        /// Try to get a color by its index (0-15).
        /// </summary>
        public static bool TryGetByIndex(int index, out SetListColors color)
        {
            if (index >= 0 && index < AllColors.Length)
            {
                color = AllColors[index];
                return true;
            }
            color = default;
            return false;
        }

        /// <summary>
        /// Get a color by index, returning Default as fallback for out-of-range values.
        /// </summary>
        public static SetListColors GetByIndexOrDefault(int index)
            => TryGetByIndex(index, out var c) ? c : Default;

        /// <summary>
        /// Convert to System.Drawing.Color.
        /// </summary>
        public Color ToDrawingColor() => Color.FromArgb(R, G, B);

        public override string ToString() => $"{DisplayName} (#{R:X2}{G:X2}{B:X2})";
    }
}
