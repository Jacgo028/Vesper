using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnityTranslator.App;

/// <summary>Use the actual game's executable icon; artwork never identifies a backend.</summary>
public sealed class GameIconConverter : IValueConverter
{
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string ?? "";
        if (_icons.TryGetValue(path, out var cached)) return cached;
        if (File.Exists(path))
        {
            var info = new ShellFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
            if (result != IntPtr.Zero && info.Icon != IntPtr.Zero)
            {
                try
                {
                    var icon = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
                    icon.Freeze(); _icons[path] = icon; return icon;
                }
                finally { DestroyIcon(info.Icon); }
            }
        }
        return DependencyProperty.UnsetValue;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
