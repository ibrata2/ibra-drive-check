using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using IbraDriveCheck.Core.Localization;

namespace IbraDriveCheck.App.Infrastructure;

/// <summary>مصدر الترجمة للواجهة: يتغير كل النص فوراً عند تبديل اللغة.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private string _lang = "ar";
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Lang
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flow)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsArabic)));
            LanguageChanged?.Invoke();
        }
    }

    public bool IsArabic => _lang != "en";
    public FlowDirection Flow => IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public event Action? LanguageChanged;

    public string this[string key] => Strings.Get(_lang, key);

    public static string T(string key) => Strings.Get(I._lang, key);
    public static string F(string key, params object[] args) => string.Format(T(key), args);
}

/// <summary>{l:T مفتاح} داخل XAML → نص مترجم يتحدث مع تبديل اللغة.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public TExtension() { }
    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var b = new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay };
        return b.ProvideValue(serviceProvider);
    }
}
