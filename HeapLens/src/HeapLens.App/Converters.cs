using System.Globalization;
using System.Windows.Data;

namespace HeapLens.App;

/// <summary>字节数 → 人类可读字符串（如 "28.82 MB"）。</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long b) return "";
        string[] units = { "B", "KB", "MB", "GB" };
        double v = Math.Abs(b);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{(b < 0 ? "-" : "")}{v:0.##} {units[u]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>地址 → 十六进制字符串。</summary>
public sealed class HexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ulong a ? $"0x{a:X12}" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
