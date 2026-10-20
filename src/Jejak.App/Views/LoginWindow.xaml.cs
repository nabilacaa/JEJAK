using System.Windows;
using Jejak.App.Services;

namespace Jejak.App.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => JudulGelap.Terapkan(this, App.Layanan.GetService(typeof(ThemeService)) is ThemeService t && t.Gelap);
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(KotakUsername.Text)) KotakUsername.Focus();
            else KotakPassword.Focus();
        };
    }
}
