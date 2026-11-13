using System.Globalization;
using System.Windows.Data;

namespace HeapLens.App;

/// <summary>字节数 → 人类可读字符串（如 "28.82 MB"）。</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 兼容 long 与 ulong（SegmentStat.Length/Committed/Reserved 等为 ulong）
        double b;
        bool neg = false;
        switch (value)
        {
            case long l: b = l; neg = l < 0; break;
            case ulong ul: b = ul; break;
            case int i: b = i; neg = i < 0; break;
            case uint ui: b = ui; break;
            default: return "";
        }
        string[] units = { "B", "KB", "MB", "GB" };
        double v = Math.Abs(b);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{(neg ? "-" : "")}{v:0.##} {units[u]}";
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
