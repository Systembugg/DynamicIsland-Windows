using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Timer
{
    public partial class AppleTimerRingControl : UserControl
    {
        public AppleTimerRingControl()
        {
            InitializeComponent();
        }

        public void SetProgress(double fraction)
        {
            fraction = Math.Clamp(fraction, 0.0, 1.0);

            double radius = 7.9;
            double cx = 10.0;
            double cy = 10.0;

            if (fraction <= 0.001)
            {
                TimerArcPath.Data = null;
                return;
            }

            if (fraction >= 0.999)
            {
                TimerArcPath.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
                return;
            }

            double angle = fraction * 360.0;
            double radians = (angle - 90.0) * Math.PI / 180.0;
            double endX = cx + radius * Math.Cos(radians);
            double endY = cy + radius * Math.Sin(radians);

            bool isLargeArc = angle > 180.0;

            var figure = new PathFigure
            {
                StartPoint = new Point(cx, cy - radius),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            TimerArcPath.Data = geo;
        }
    }
}
