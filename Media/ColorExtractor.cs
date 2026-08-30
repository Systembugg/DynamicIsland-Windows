using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Media
{
    public static class ColorExtractor
    {
        public static (Color Primary, Color Secondary)? ExtractVibrantPalette(BitmapSource? image)
        {
            if (image == null) return null;

            try
            {
                int width = image.PixelWidth;
                int height = image.PixelHeight;
                if (width <= 0 || height <= 0) return null;

                // Format to Bgra32
                BitmapSource formatted = image;
                if (image.Format != PixelFormats.Bgra32)
                {
                    formatted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                }

                int stride = width * 4;
                byte[] pixels = new byte[height * stride];
                formatted.CopyPixels(pixels, stride, 0);

                var candidates = new List<(Color Color, double Score)>();

                int stepX = Math.Max(1, width / 30);
                int stepY = Math.Max(1, height / 30);

                for (int y = 0; y < height; y += stepY)
                {
                    for (int x = 0; x < width; x += stepX)
                    {
                        int idx = y * stride + x * 4;
                        if (idx + 3 >= pixels.Length) continue;

                        byte b = pixels[idx];
                        byte g = pixels[idx + 1];
                        byte r = pixels[idx + 2];
                        byte a = pixels[idx + 3];

                        if (a < 100) continue; // Skip transparency

                        RgbToHsl(r, g, b, out double h, out double s, out double l);

                        // Skip pure black (L < 0.08) and pure white (L > 0.92)
                        if (l < 0.08 || l > 0.92) continue;

                        // Vibrant score favors high saturation
                        double score = (s * 3.0) + (1.0 - Math.Abs(l - 0.50) * 1.5);
                        candidates.Add((Color.FromRgb(r, g, b), score));
                    }
                }

                if (candidates.Count == 0) return null;

                // Sort by vibrancy score
                var bestColor = candidates.OrderByDescending(c => c.Score).First().Color;

                RgbToHsl(bestColor.R, bestColor.G, bestColor.B, out double bh, out double bs, out double bl);

                // If the best color has virtually zero saturation, fallback
                if (bs < 0.12) return null;

                // Generate complementary gradient stop
                double secondHue = (bh + 15.0) % 360.0;
                double secondLight = Math.Clamp(bl * 0.80, 0.25, 0.80);
                double secondSat = Math.Clamp(bs * 1.15, 0.35, 1.0);
                var secondaryColor = HslToRgb(secondHue, secondSat, secondLight);

                return (bestColor, secondaryColor);
            }
            catch
            {
                return null;
            }
        }

        private static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
        {
            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;

            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            l = (max + min) / 2.0;

            if (Math.Abs(max - min) < 0.0001)
            {
                h = 0;
                s = 0;
            }
            else
            {
                double d = max - min;
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

                if (Math.Abs(max - rf) < 0.0001)
                    h = (gf - bf) / d + (gf < bf ? 6.0 : 0.0);
                else if (Math.Abs(max - gf) < 0.0001)
                    h = (bf - rf) / d + 2.0;
                else
                    h = (rf - gf) / d + 4.0;

                h *= 60.0;
            }
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double r, g, b;
            if (Math.Abs(s) < 0.0001)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;
                r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
                g = HueToRgb(p, q, h / 360.0);
                b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
            }

            return Color.FromRgb((byte)Math.Clamp(r * 255.0, 0, 255), (byte)Math.Clamp(g * 255.0, 0, 255), (byte)Math.Clamp(b * 255.0, 0, 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1.0;
            if (t > 1) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }
    }
}
