using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Call
{
    public partial class CallWaveformControl : UserControl
    {
        private readonly Border[] _bars;
        private readonly double[] _currentHeights = new double[14];
        private readonly double[] _targetHeights = new double[14];
        private readonly double[] _baseHeights = new double[] { 4, 8, 12, 14, 10, 8, 6, 7, 12, 10, 14, 11, 9, 5 };

        private readonly Stopwatch _stopwatch = new();
        private bool _isAnimating = false;

        public CallWaveformControl()
        {
            InitializeComponent();
            _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8, Bar9, Bar10, Bar11, Bar12, Bar13 };

            for (int i = 0; i < 14; i++)
            {
                _currentHeights[i] = _baseHeights[i];
                _targetHeights[i] = _baseHeights[i];
            }

            Loaded += (s, e) => StartAnimation();
            Unloaded += (s, e) => StopAnimation();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) StartAnimation();
                else StopAnimation();
            };
        }

        public void StartAnimation()
        {
            if (_isAnimating) return;
            _isAnimating = true;
            _stopwatch.Restart();
            CompositionTarget.Rendering += OnRendering;
        }

        public void StopAnimation()
        {
            if (!_isAnimating) return;
            _isAnimating = false;
            _stopwatch.Stop();
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!IsVisible) return;

            double time = _stopwatch.Elapsed.TotalSeconds;

            // Slow, organic voice speech breathing wave (Smooth ~2.2 rad/sec)
            double voiceRhythm = (Math.Sin(time * 2.2) * 0.35) + 0.65;
            double slowDrift = Math.Cos(time * 0.8) * 0.15;

            // 7 Green Bars (Left voice spectrum)
            for (int i = 0; i < 7; i++)
            {
                double phase = i * 0.55;
                double wave = (Math.Sin((time * 3.5) + phase) * 0.3) + (Math.Sin((time * 1.8) + (phase * 1.5)) * 0.2) + 0.5;
                double factor = Math.Clamp((wave * voiceRhythm) + slowDrift, 0.35, 1.1);
                _targetHeights[i] = Math.Clamp(_baseHeights[i] * factor, 3.0, 15.0);
            }

            // 7 Orange Bars (Right voice spectrum)
            for (int i = 7; i < 14; i++)
            {
                double phase = (i - 7) * 0.55;
                double wave = (Math.Sin((time * 4.0) + phase + 1.2) * 0.3) + (Math.Cos((time * 2.1) + phase) * 0.2) + 0.5;
                double factor = Math.Clamp((wave * voiceRhythm) + slowDrift, 0.35, 1.1);
                _targetHeights[i] = Math.Clamp(_baseHeights[i] * factor, 3.0, 15.0);
            }

            // Silky 60fps exponential smoothing (zero jitter, pure fluid motion)
            const double smoothingFactor = 0.14;

            for (int i = 0; i < 14; i++)
            {
                _currentHeights[i] += (_targetHeights[i] - _currentHeights[i]) * smoothingFactor;
                _bars[i].Height = Math.Max(2.5, _currentHeights[i]);
            }
        }
    }
}
