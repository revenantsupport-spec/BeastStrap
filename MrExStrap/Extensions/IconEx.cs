using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace BeastStrap.Extensions
{
    public static class IconEx
    {
        public static Icon GetSized(this Icon icon, int width, int height) => new(icon, new Size(width, height));

        public static ImageSource GetImageSource(this Icon icon, bool handleException = true)
        {
            using MemoryStream stream = new();
            icon.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);

            try
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame best = decoder.Frames[0];
                foreach (var frame in decoder.Frames)
                {
                    if (frame.PixelWidth > best.PixelWidth)
                        best = frame;
                }
                return best;
            }
            catch (Exception ex) when (handleException)
            {
                App.Logger.WriteException("IconEx::GetImageSource", ex);
                Frontend.ShowMessageBox(string.Format(Strings.Dialog_IconLoadFailed, ex.Message));
                return BootstrapperIcon.IconBloxstrap.GetIcon().GetImageSource(false);
            }
        }
    }
}
