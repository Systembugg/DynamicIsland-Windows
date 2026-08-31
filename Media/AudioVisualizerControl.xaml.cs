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
        private readonly Random rng = new Random();
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

        private readonly ScaleTransform[] scaleTransforms = new ScaleTransform[6];
        private const double BaseMaxHeight = 14.5;

        public AudioVisualizerControl()
        {
            InitializeComponent();
            bars = new Border[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5 };
            currentHeights = new double[6];
            targetHeights = new double[6];

            for (int i = 0; i < 6; i++)
            {
                var st = new ScaleTransform(1.0, 3.0 / BaseMaxHeight);
                scaleTransforms[i] = st;
                bars[i].RenderTransformOrigin = new Point(0.5, 0.5);
                bars[i].RenderTransform = st;
                bars[i].Height = BaseMaxHeight;
                currentHeights[i] = 3.0;
                targetHeights[i] = 3.0;
            }

            animTimer.Interval = TimeSpan.FromMilliseconds(40); // 25 FPS
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
            phase += 0.35;

            if (isPlaying)
            {
                // Dynamic harmonic frequencies + organic amplitude variation matching Apple Music Equalizer
                for (int i = 0; i < barCount; i++)
                {
                    double s1 = Math.Sin((phase * 1.2) + (i * 1.3));
                    double s2 = Math.Cos((phase * 0.7) - (i * 0.9));
                    double r = rng.NextDouble();
                    
                    double mag = Math.Abs((s1 * 0.5) + (s2 * 0.3) + (r * 0.4));
                    mag = Math.Clamp(mag, 0.2, 1.0);

                    targetHeights[i] = 3.0 + (mag * 11.5); // 3px to 14.5px
                }
            }

            // Smooth interpolation via GPU ScaleTransform (0 CPU Layout Passes)
            bool stillMoving = false;
            for (int i = 0; i < barCount; i++)
            {
                double diff = targetHeights[i] - currentHeights[i];
                if (Math.Abs(diff) > 0.15)
                {
                    currentHeights[i] += diff * 0.45;
                    scaleTransforms[i].ScaleY = currentHeights[i] / BaseMaxHeight;
                    stillMoving = true;
                }
                else
                {
                    currentHeights[i] = targetHeights[i];
                    scaleTransforms[i].ScaleY = currentHeights[i] / BaseMaxHeight;
                }
            }

            if (!isPlaying && !stillMoving)
            {
                animTimer.Stop();
            }
        }
    }
}
