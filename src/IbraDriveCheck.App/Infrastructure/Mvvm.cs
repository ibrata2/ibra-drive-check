using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v ? v : value != null && !(value is string s && s.Length == 0) && !(value is int i && i == 0);
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class VerdictColors
{
    public static Brush For(Verdict v) => new SolidColorBrush(v switch
    {
        Verdict.Genuine => Color.FromRgb(0x16, 0xA3, 0x4A),
        Verdict.FakeCapacity => Color.FromRgb(0xE6, 0x00, 0x12),
        Verdict.ReadWriteErrors => Color.FromRgb(0xC2, 0x41, 0x0C),
        Verdict.PoorPerformance => Color.FromRgb(0xCA, 0x8A, 0x04),
        _ => Color.FromRgb(0x6B, 0x72, 0x80),
    });

    public static string Glyph(Verdict v) => v switch
    {
        Verdict.Genuine => "",
        Verdict.FakeCapacity => "",
        Verdict.ReadWriteErrors => "",
        Verdict.PoorPerformance => "",
        _ => "",
    };

    public static Brush Cell(CellState s) => s switch
    {
        CellState.Good => Frozen(0x22, 0xC5, 0x5E),
        CellState.Failed => Frozen(0xEF, 0x44, 0x44),
        CellState.WriteError => Frozen(0xF9, 0x73, 0x16),
        CellState.ReadError => Frozen(0xA8, 0x55, 0xF7),
        CellState.Slow => Frozen(0xEA, 0xB3, 0x08),
        CellState.Testing => Frozen(0x3B, 0x82, 0xF6),
        _ => Frozen(0x9C, 0xA3, 0xAF),
    };

    private static readonly Dictionary<int, Brush> Cache = new();
    private static Brush Frozen(byte r, byte g, byte b)
    {
        int k = (r << 16) | (g << 8) | b;
        if (Cache.TryGetValue(k, out var br)) return br;
        var s = new SolidColorBrush(Color.FromRgb(r, g, b));
        s.Freeze();
        Cache[k] = s;
        return s;
    }

    public static string MediaGlyph(MediaKind k) => k switch
    {
        MediaKind.UsbFlash => "",
        MediaKind.SdCard => "",
        MediaKind.ExternalHdd => "",
        MediaKind.ExternalSsd => "",
        MediaKind.InternalHdd => "",
        MediaKind.InternalSsd => "",
        _ => "",
    };
}

/// <summary>صف (عنوان، قيمة) لعرض المعلومات.</summary>
public sealed record InfoRow(string Label, string Value, bool Highlight = false);
