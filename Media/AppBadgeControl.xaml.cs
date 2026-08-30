using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Media
{
    public partial class AppBadgeControl : UserControl
    {
        private static BitmapImage? imgChrome;
        private static BitmapImage? imgSpotify;
        private static BitmapImage? imgAppleMusic;
        private static BitmapImage? imgEdge;
        private static BitmapImage? imgYouTube;

        private static readonly Geometry geoChrome = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 3c2.4 0 4.5 1.2 5.8 3h-6.2c-.8 0-1.5.4-1.9 1l-2.6 4.5C7 13 7 12.5 7 12c0-2.8 2.2-5 5-5zm-5.7 4.7l2.8 4.8c.4.7 1.1 1.2 1.9 1.4v4.9C7.4 20.2 5 16.5 5 12.2c0-.9.2-1.7.3-2.5zm6.7 9.1v-4.9c.8 0 1.6-.4 2.1-1.1l2.8-4.8c.7 1.2 1.1 2.6 1.1 4 0 3.7-2.6 6.8-6 7.8z");
        private static readonly Geometry geoSpotify = Geometry.Parse("M12 2C6.477 2 2 6.477 2 12c0 5.523 4.477 10 10 10s10-4.477 10-10c0-5.523-4.477-10-10-10zm4.586 14.424c-.18.295-.563.387-.857.207-2.35-1.436-5.308-1.76-8.793-.963-.335.077-.67-.133-.746-.468-.077-.334.132-.67.467-.746 3.808-.87 7.076-.496 9.722 1.115.294.18.386.562.207.855zm1.225-2.724c-.226.367-.708.482-1.075.257-2.69-1.653-6.79-2.132-9.97-1.167-.413.125-.85-.106-.975-.519-.125-.413.106-.85.519-.975 3.632-1.102 8.147-.568 11.244 1.33.367.225.482.707.257 1.074zm.105-2.835C14.692 8.95 9.375 8.775 6.297 9.71c-.494.15-1.018-.13-1.168-.624-.15-.494.13-1.018.624-1.168 3.532-1.072 9.404-.866 13.115 1.337.445.264.59.838.327 1.282-.264.444-.838.59-1.282.327z");
        private static readonly Geometry geoAppleMusic = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm3.64 6.74l-4.14 1.22c-.22.06-.38.25-.38.48v5.52c-.44-.27-.97-.43-1.54-.43-1.42 0-2.58.97-2.58 2.16s1.16 2.16 2.58 2.16 2.58-.97 2.58-2.16V12.1l3.52-1.04v3.42c-.44-.27-.97-.43-1.54-.43-1.42 0-2.58.97-2.58 2.16s1.16 2.16 2.58 2.16 2.58-.97 2.58-2.16V8.97c0-.28-.24-.49-.52-.45l-.1.02z");
        private static readonly Geometry geoYouTube = Geometry.Parse("M21.58 7.19c-.23-.86-.91-1.54-1.77-1.77C18.25 5 12 5 12 5s-6.25 0-7.81.42c-.86.23-1.54.91-1.77 1.77C2 8.75 2 12 2 12s0 3.25.42 4.81c.23.86.91 1.54 1.77 1.77C5.75 19 12 19 12 19s6.25 0 7.81-.42c.86-.23 1.54-.91 1.77-1.77C22 15.25 22 12 22 12s0-3.25-.42-4.81zM10 15V9l5.2 3-5.2 3z");
        private static readonly Geometry geoGlobe = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 17.93c-3.95-.49-7-3.85-7-7.93 0-.62.08-1.21.21-1.79L9 15v1c0 1.1.9 2 2 2v1.93zm6.9-2.54c-.26-.81-1-1.39-1.9-1.39h-1v-3c0-.55-.45-1-1-1H8v-2h2c.55 0 1-.45 1-1V7h2c1.1 0 2-.9 2-2v-.41c2.93 1.19 5 4.06 5 7.41 0 2.08-.8 3.97-2.1 5.39z");

        static AppBadgeControl()
        {
            try
            {
                imgChrome = LoadBrandImage("logo_chrome.png");
                imgSpotify = LoadBrandImage("logo_spotify.png");
                imgAppleMusic = LoadBrandImage("logo_applemusic.png");
                imgEdge = LoadBrandImage("logo_edge.png");
                imgYouTube = LoadBrandImage("logo_youtube.png");
            }
            catch { }
        }

        private static BitmapImage? LoadBrandImage(string fileName)
        {
            // 1. Try Pack URI from compiled resources
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
                var bmp = new BitmapImage(uri);
                bmp.Freeze();
                return bmp;
            }
            catch { }

            // 2. Try Local Base Directory File
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }

            return null;
        }

        public AppBadgeControl()
        {
            InitializeComponent();
            SetAppSource(MediaAppSource.Chrome);
        }

        public void SetAppSource(MediaAppSource source)
        {
            BitmapImage? bmp = null;
            Geometry vectorGeo = geoChrome;
            Brush vectorBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4));

            switch (source)
            {
                case MediaAppSource.Chrome:
                case MediaAppSource.Brave:
                case MediaAppSource.Firefox:
                    bmp = imgChrome;
                    vectorGeo = geoChrome;
                    vectorBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4));
                    break;

                case MediaAppSource.Spotify:
                    bmp = imgSpotify;
                    vectorGeo = geoSpotify;
                    vectorBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
                    break;

                case MediaAppSource.AppleMusic:
                    bmp = imgAppleMusic;
                    vectorGeo = geoAppleMusic;
                    vectorBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0x24, 0x3C));
                    break;

                case MediaAppSource.Edge:
                    bmp = imgEdge;
                    vectorGeo = geoGlobe;
                    vectorBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                    break;

                case MediaAppSource.YouTube:
                    bmp = imgYouTube ?? imgChrome;
                    vectorGeo = geoYouTube;
                    vectorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
                    break;

                default:
                    bmp = imgChrome;
                    vectorGeo = geoChrome;
                    vectorBrush = new SolidColorBrush(Color.FromRgb(0x42, 0x85, 0xF4));
                    break;
            }

            if (bmp != null)
            {
                ImgBadge.Source = bmp;
                ImgBadge.Visibility = Visibility.Visible;
                VectorBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                VectorBadge.Data = vectorGeo;
                VectorBadge.Fill = vectorBrush;
                VectorBadge.Visibility = Visibility.Visible;
                ImgBadge.Visibility = Visibility.Collapsed;
            }
        }
    }
}
