using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland.Media
{
    public partial class AudioVisualizerControl : UserControl
    {
        private readonly DispatcherTimer animTimer = new DispatcherTimer(DispatcherPriority.Render);
        private readonly Border[] bars;
        private readonly double[] currentHeights;
        private readonly double[] targetHeights;
        private readonly double[] velocities;
        private double phase = 0;

        public void SetAccentFromImage(BitmapSource? thumbnail, MediaAppSource appSource)
        {
            if (thumbnail != null)
            {
                var palette = ColorExtractor.ExtractVibrantPalette(thumbnail);
                if (palette.HasValue)
                {
                    var gradient = new LinearGradientBrush(
                        palette.Value.Primary,
                        palette.Value.Secondary,
                        new Point(0, 0),
                        new Point(0, 1)
                    );
                    ApplyCustomBrush(gradient);
                    return;
                }
            }

            // Fallback to signature App Source color
            SetAccentColor(appSource);
        }

        public void ApplyCustomBrush(Brush brush)
        {
            if (bars != null)
            {
                foreach (var bar in bars)
                {
                    if (bar != null) bar.Background = brush;
                }
            }
        }

        public void SetAccentColor(MediaAppSource appSource)
        {
            Brush brush;
            switch (appSource)
            {
                case MediaAppSource.Spotify:
                    brush = new LinearGradientBrush(
                        Color.FromRgb(0x22, 0xE6, 0x6B),
                        Color.FromRgb(0x1D, 0xB9, 0x54),
                        new Point(0, 0),
                        new Point(0, 1)
                    );
                    break;

                case MediaAppSource.YouTube:
                case MediaAppSource.Chrome:
                case MediaAppSource.Brave:
                case MediaAppSource.Firefox:
                    brush = new LinearGradientBrush(
                        Color.FromRgb(0xFF, 0x4B, 0x4B),
                        Color.FromRgb(0xFF, 0x00, 0x00),
                        new Point(0, 0),
                        new Point(0, 1)
                    );
                    break;

                case MediaAppSource.AppleMusic:
                default:
                    brush = new LinearGradientBrush(
                        Color.FromRgb(0xC7, 0x59, 0xC9),
                        Color.FromRgb(0xFA, 0x2D, 0x48),
                        new Point(0, 0),
                        new Point(0, 1)
                    );
                    break;
            }

            ApplyCustomBrush(brush);
        }

        private bool isPlaying = false;
        public bool IsPlaying
        {
            get => isPlaying;
            set
            {
                isPlaying = value;
                if (isPlaying)
                {
                    if (!animTimer.IsEnabled) animTimer.Start();
                }
                else
                {
                    for (int i = 0; i < currentHeights.Length; i++)
                    {
                        targetHeights[i] = 3.0;
                    }
                }
            }
        }

        private int barCount = 4;
        public int BarCount
        {
            get => barCount;
            set
            {
                barCount = Math.Clamp(value, 4, 6);
                UpdateBarVisibility();
            }
        }

        public AudioVisualizerControl()
        {
            InitializeComponent();
            bars = new Border[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5 };
            currentHeights = new double[6];
            targetHeights = new double[6];
            velocities = new double[6];

            for (int i = 0; i < 6; i++)
            {
                currentHeights[i] = 3.0;
                targetHeights[i] = 3.0;
                velocities[i] = 0.0;
            }

            animTimer.Interval = TimeSpan.FromMilliseconds(16); // 60 FPS smooth rendering loop
            animTimer.Tick += AnimTimer_Tick;

            UpdateBarVisibility();
        }

        private void UpdateBarVisibility()
        {
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i].Visibility = i < barCount ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AnimTimer_Tick(object? sender, EventArgs e)
        {
            phase += 0.28;

            if (isPlaying)
            {
                // Default Apple Smooth Harmonic Rhythmic Wave
                for (int i = 0; i < barCount; i++)
                {
                    double s1 = Math.Sin((phase * 0.8) + (i * 1.15));
                    double s2 = Math.Cos((phase * 0.45) - (i * 0.75));
                    double mag = Math.Abs((s1 * 0.55) + (s2 * 0.45));
                    targetHeights[i] = 3.0 + (Math.Clamp(mag, 0.12, 0.95) * 10.5);
                }
            }
            else
            {
                for (int i = 0; i < barCount; i++)
                {
                    targetHeights[i] = 3.0;
                }
            }

            // Apple Critically Damped Spring Physics (stiffness 0.20, damping 0.76)
            double stiffness = 0.20;
            double damping = 0.76;
            bool stillMoving = false;

            for (int i = 0; i < barCount; i++)
            {
                double displacement = targetHeights[i] - currentHeights[i];
                velocities[i] = (velocities[i] * damping) + (displacement * stiffness);
                currentHeights[i] += velocities[i];

                currentHeights[i] = Math.Clamp(currentHeights[i], 3.0, 15.0);
                bars[i].Height = currentHeights[i];

                if (Math.Abs(displacement) > 0.05 || Math.Abs(velocities[i]) > 0.05)
                {
                    stillMoving = true;
                }
            }

            if (!isPlaying && !stillMoving)
            {
                animTimer.Stop();
            }
        }
    }
}
