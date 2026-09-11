using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace DynamicIsland.AI
{
    public static class ScreenCaptureHelper
    {
        public static (string? filePath, string? base64Image) CaptureScreen()
        {
            try
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                // Downscale if too large to ensure fast upload & memory efficiency
                int targetWidth = Math.Min(bounds.Width, 1280);
                int targetHeight = (int)(bounds.Height * ((double)targetWidth / bounds.Width));
                using var resized = new Bitmap(bmp, new Size(targetWidth, targetHeight));

                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "Screenshots");
                Directory.CreateDirectory(folder);

                string filename = $"snap_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string fullPath = Path.Combine(folder, filename);

                // Save as JPEG with 80% quality
                var encoder = GetEncoder(ImageFormat.Jpeg);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 80L);

                if (encoder != null)
                {
                    resized.Save(fullPath, encoder, encoderParams);
                }
                else
                {
                    resized.Save(fullPath, ImageFormat.Jpeg);
                }

                byte[] imageBytes = File.ReadAllBytes(fullPath);
                string base64 = Convert.ToBase64String(imageBytes);

                return (fullPath, base64);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenCaptureHelper] Error: {ex.Message}");
                return (null, null);
            }
        }

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return null;
        }
    }
}
