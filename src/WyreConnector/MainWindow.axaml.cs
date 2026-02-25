using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using WyreConnector.Localization;

namespace WyreConnector;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // Apply localization if service is available
        var localizer = ModuleHostBootstrapper.ServiceProvider?.GetService<LocalizationService>();
        if (localizer != null)
        {
            var noRootModulesText = this.FindControl<TextBlock>("NoRootModulesText");
            if (noRootModulesText != null)
                noRootModulesText.Text = localizer.GetString("NoRootModulesInstalled");
                
            var installRootModuleText = this.FindControl<TextBlock>("InstallRootModuleText");
            if (installRootModuleText != null)
                installRootModuleText.Text = localizer.GetString("InstallRootModuleToGetStarted");
                
            var exitButton = this.FindControl<Button>("ExitButton");
            if (exitButton != null)
                exitButton.Content = localizer.GetString("Exit");
        }
    }

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        var url = "https://wyre.zombidev.me/modules";
        try
        {
            Process.Start(url);
        }
        catch
        {
            // hack because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                url = url.Replace("&", "^&");
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw;
            }
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

