using System;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Call
{
    public partial class CallWaveformControl : UserControl
    {
        private readonly Border[] _bars;
        private readonly double[] _currentHeights = new double[14];
        private readonly double[] _targetHeights = new double[14];
        private readonly double[] _velocities = new double[14];
        private readonly double[] _baseHeights = new double[] { 4, 8, 12, 14, 10, 8, 6, 7, 12, 10, 14, 11, 9, 5 };

        private readonly Random _rand = new();
        private bool _isAnimating = false;
        private int _frameCounter = 0;

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
            CompositionTarget.Rendering += OnRendering;
        }

        public void StopAnimation()
        {
            if (!_isAnimating) return;
            _isAnimating = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!IsVisible) return;

            _frameCounter++;
            if (_frameCounter % 4 == 0)
            {
                // Generate natural speech pattern fluctuations
                double greenWave = (Math.Sin(_frameCounter * 0.15) + 1.0) * 0.5;
                double orangeWave = (Math.Cos(_frameCounter * 0.18) + 1.0) * 0.5;

                for (int i = 0; i < 7; i++)
                {
                    double factor = 0.3 + (greenWave * 0.7) + (_rand.NextDouble() * 0.4 - 0.2);
                    _targetHeights[i] = Math.Clamp(_baseHeights[i] * factor, 3.0, 15.5);
                }

                for (int i = 7; i < 14; i++)
                {
                    double factor = 0.3 + (orangeWave * 0.7) + (_rand.NextDouble() * 0.4 - 0.2);
                    _targetHeights[i] = Math.Clamp(_baseHeights[i] * factor, 3.0, 15.5);
                }
            }

            // Spring Physics interpolation
            const double stiffness = 0.25;
            const double damping = 0.72;

            for (int i = 0; i < 14; i++)
            {
                double displacement = _targetHeights[i] - _currentHeights[i];
                _velocities[i] = (_velocities[i] * damping) + (displacement * stiffness);
                _currentHeights[i] += _velocities[i];

                _bars[i].Height = Math.Max(2.5, _currentHeights[i]);
            }
        }
    }
}
