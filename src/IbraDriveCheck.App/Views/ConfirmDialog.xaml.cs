using System.Windows;
using IbraDriveCheck.App.Infrastructure;

namespace IbraDriveCheck.App.Views;

/// <summary>موافقة صريحة قبل أي فحص يكتب على الوحدة (مع كتابة الحجم للتأكيد في الفحص المدمر).</summary>
public partial class ConfirmDialog : Window
{
    private readonly string? _typed;

    public ConfirmDialog(string drive, string kind, string message, string? typedConfirmation, bool danger)
    {
        InitializeComponent();
        _typed = typedConfirmation;
        DriveText.Text = drive;
        KindText.Text = kind;
        MessageText.Text = message;
        if (typedConfirmation != null)
        {
            TypedPanel.Visibility = Visibility.Visible;
            TypedHint.Text = typedConfirmation;
        }
        if (!danger) IconText.Text = "";
        SourceInitialized += (_, _) => Dwm.ApplyDarkTitle(this, ThemeService.Current == "dark");
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        bool typedOk = _typed == null || string.Equals(TypedBox.Text.Trim(), _typed, StringComparison.OrdinalIgnoreCase);
        GoButton.IsEnabled = BackupCheck.IsChecked == true && typedOk;
    }

    private void OnGo(object sender, RoutedEventArgs e) => DialogResult = true;
}
