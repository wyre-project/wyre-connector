using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Tomlyn;
using Wyre.Core.Messaging;

namespace WyreConnector;

public record ConfigReloadedMessage(object Config);
public record ConfigReloadFailedMessage(string Error);

public class ConfigWatcher : IHostedService
{
    private readonly FileSystemWatcher _watcher;
    private readonly IMessageBus _bus;
    private readonly string _configPath;
    
    // Debounce — editors often write files multiple times on save
    private readonly Timer _debounce;
    private const int DebounceMs = 500;
    
    public ConfigWatcher(IMessageBus bus, string configPath)
    {
        _bus = bus;
        _configPath = configPath;
        _debounce = new Timer(OnDebounced, null, 
            Timeout.Infinite, Timeout.Infinite);
        
        var dir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(dir))
        {
            dir = AppContext.BaseDirectory;
        }

        _watcher = new FileSystemWatcher(
            dir,
            Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = false
        };
        
        _watcher.Changed += (_, _) =>
            _debounce.Change(DebounceMs, Timeout.Infinite);
    }
    
    private void OnDebounced(object? _)
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            
            // In a real app, we would parse to a specific model
            var config = Toml.ToModel(File.ReadAllText(_configPath));
            _bus.Publish(new ConfigReloadedMessage(config));
        }
        catch (Exception ex)
        {
            // Bad TOML — notify user, keep running with current config
            _bus.Publish(new ConfigReloadFailedMessage(ex.Message));
        }
    }
    
    public Task StartAsync(CancellationToken ct)
    {
        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken ct)
    {
        _watcher.EnableRaisingEvents = false;
        return Task.CompletedTask;
    }
}
