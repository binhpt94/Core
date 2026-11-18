using System;
using System.Collections.Generic;

namespace Game.Core.DevTools
{
    /// <summary>
    /// A screen to lay a view out on: size and safe-area insets in physical pixels (portrait).
    /// Converted to canvas units with the same formula as uGUI's CanvasScaler (Scale With Screen Size, match).
    /// </summary>
    public sealed class DeviceProfile
    {
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int SafeTop { get; }
        public int SafeBottom { get; }
        public int SafeLeft { get; }
        public int SafeRight { get; }

        public DeviceProfile(string name, int width, int height, int safeTop = 0, int safeBottom = 0, int safeLeft = 0, int safeRight = 0)
        {
            Name = name;
            Width = width;
            Height = height;
            SafeTop = safeTop;
            SafeBottom = safeBottom;
            SafeLeft = safeLeft;
            SafeRight = safeRight;
        }

        /// <summary>Small, fixed set: one per layout risk (base, tall, notch, wide). Add a device only for a new risk.</summary>
        public static IReadOnlyList<DeviceProfile> Standard() => new[]
        {
            new DeviceProfile("phone-16x9", 1080, 1920),
            new DeviceProfile("phone-20x9", 1080, 2400, safeTop: 96),
            new DeviceProfile("phone-notch", 1170, 2532, safeTop: 141, safeBottom: 102),
            new DeviceProfile("tablet-4x3", 1536, 2048),
        };

        /// <summary>CanvasScaler.ScaleWithScreenSize: scale = 2^lerp(log2(w/refW), log2(h/refH), match).</summary>
        public double Scale(int referenceWidth, int referenceHeight, double matchWidthOrHeight)
        {
            double logW = Math.Log((double)Width / referenceWidth, 2);
            double logH = Math.Log((double)Height / referenceHeight, 2);
            return Math.Pow(2, logW + (logH - logW) * matchWidthOrHeight);
        }

        /// <summary>Canvas size in canvas units (what the layout actually gets).</summary>
        public LayoutRect Canvas(int referenceWidth, int referenceHeight, double match)
        {
            double s = Scale(referenceWidth, referenceHeight, match);
            return new LayoutRect(0, 0, (int)Math.Round(Width / s), (int)Math.Round(Height / s));
        }

        /// <summary>Safe area in canvas units.</summary>
        public LayoutRect SafeArea(int referenceWidth, int referenceHeight, double match)
        {
            double s = Scale(referenceWidth, referenceHeight, match);
            int Px(int v) => (int)Math.Round(v / s);
            var c = Canvas(referenceWidth, referenceHeight, match);
            return new LayoutRect(Px(SafeLeft), Px(SafeTop), c.W - Px(SafeLeft) - Px(SafeRight), c.H - Px(SafeTop) - Px(SafeBottom));
        }

        public override string ToString() => Name;
    }
}
