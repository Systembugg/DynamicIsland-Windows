using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Dock
{
    public partial class DockStatusRingControl : UserControl
    {
        public DockStatusRingControl()
        {
            InitializeComponent();
            SetStatus(DockShelfStatus.Docked, 1);
        }

        public void SetStatus(DockShelfStatus status, int count = 1)
        {
            if (status == DockShelfStatus.Docked)
            {
                // Full-Filled Blue Ring with Count Number in center
                RingArcPath.Stroke = new SolidColorBrush(Color.FromRgb(10, 132, 255)); // Apple Blue (#0A84FF)
                TxtDockCount.Foreground = new SolidColorBrush(Color.FromRgb(10, 132, 255));

                if (count <= 9)
                {
                    TxtDockCount.Text = (count <= 0 ? 1 : count).ToString();
                    TxtDockCount.FontSize = 8.5;
                }
                else if (count <= 99)
                {
                    // Full 2 Digits visible cleanly (e.g. 10, 24, 99)
                    TxtDockCount.Text = count.ToString();
                    TxtDockCount.FontSize = 7.0;
                }
                else
                {
                    // 3+ Digits -> 99+
                    TxtDockCount.Text = "99+";
                    TxtDockCount.FontSize = 5.8;
                }

                TxtDockCount.Visibility = Visibility.Visible;
                GreenCheckmark.Visibility = Visibility.Collapsed;
                DrawArc(1.0);
            }
            else if (status == DockShelfStatus.Used)
            {
                // 100% Full-Filled Green Ring with Checkmark
                RingArcPath.Stroke = new SolidColorBrush(Color.FromRgb(48, 209, 88)); // Apple Green (#30D158)
                TxtDockCount.Visibility = Visibility.Collapsed;
                GreenCheckmark.Visibility = Visibility.Visible;
                DrawArc(1.0);
            }
            else
            {
                RingArcPath.Data = null;
                TxtDockCount.Visibility = Visibility.Collapsed;
                GreenCheckmark.Visibility = Visibility.Collapsed;
            }
        }

        private void DrawArc(double fraction)
        {
            double radius = 7.9;
            double cx = 10.0;
            double cy = 10.0;

            if (fraction >= 0.999)
            {
                RingArcPath.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
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
            RingArcPath.Data = geo;
        }
    }
}
