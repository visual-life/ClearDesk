using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClearDesk
{
    internal static class AppBrand
    {
        static ImageSource icon;
        public static ImageSource Icon
        {
            get
            {
                if (icon == null)
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearDesk.AppIcon.ico"))
                    {
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var frame = decoder.Frames.OrderBy(f => f.PixelWidth).Last();
                        frame.Freeze();
                        icon = frame;
                    }

                return icon;
            }
        }
    }
}
