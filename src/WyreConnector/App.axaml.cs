using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;

namespace WyreConnector;

public partial class App : Application
{
    private TrayIcon? _tray;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            
            // Intercept close → minimize to tray instead
            window.Closing += (_, e) =>
            {
                e.Cancel = true;
                window.Hide();
            };
            
            _tray = new TrayIcon
            {
                ToolTipText = "Wyre Connector",
                Menu = new NativeMenu
                {
                    Items =
                    {
                        new NativeMenuItem("Open") { Command = new RelayCommand(window.Show) },
                        new NativeMenuItemSeparator(),
                        new NativeMenuItem("Exit") { Command = new RelayCommand(() =>
                        {
                            _tray!.IsVisible = false;
                            desktop.Shutdown();
                        })}
                    }
                }
            };
            
            _tray.Clicked += (_, _) =>
            {
                window.Show();
                window.Activate();
            };
            
            _tray.IsVisible = true;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
