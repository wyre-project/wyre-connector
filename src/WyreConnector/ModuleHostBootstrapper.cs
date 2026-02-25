using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wyre.Core.Messaging;
using Wyre.ModuleHost;
using Wyre.ModuleHost.Contracts;
using WyreConnector.Localization;

namespace WyreConnector;

public static class ModuleHostBootstrapper
{
    private static ModuleHostImpl? _host;
    public static IServiceProvider? ServiceProvider { get; private set; }

    private static async Task<ModuleHostImpl> GetOrCreateHostAsync()
    {
        if (_host != null) return _host;

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        // Initialize language from config or autodetect
        var preferredLanguage = config["Language"];
        LocalizationService.InitializeLanguage(preferredLanguage);

        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddSingleton<LocalizationService>();

        services.AddSingleton<IMessageBus, MessageBus>();
        
        ServiceProvider = services.BuildServiceProvider();
        var bus = ServiceProvider.GetRequiredService<IMessageBus>();
        var logger = ServiceProvider.GetRequiredService<ILogger<ModuleHostImpl>>();
        
        var modulesPath = Path.Combine(AppContext.BaseDirectory, "modules");
        if (!Directory.Exists(modulesPath))
        {
            Directory.CreateDirectory(modulesPath);
        }

        _host = new ModuleHostImpl(
            bus,
            ServiceProvider,
            config,
            logger,
            modulesPath,
            new Version(1, 0, 0)
        );

        await _host.LoadDirectoryAsync(modulesPath);
        return _host;
    }

    public static async Task RunAsync(string[] args)
    {
        var host = await GetOrCreateHostAsync();

        var rootModules = host.All.OfType<IRootModule>().ToList();

        string? targetRootModule = null;
        if (args.Length >= 2 && args[0] == "--root")
        {
            targetRootModule = args[1];
        }

        if (targetRootModule != null)
        {
            var module = rootModules.FirstOrDefault(m => m.Id == targetRootModule);
            if (module != null)
            {
                await module.RunAsync(host, args, default);
                return;
            }
            Console.WriteLine($"Error: Root module '{targetRootModule}' not found.");
            return;
        }

        if (rootModules.Any())
        {
            // Run the first root module found
            var rootModule = rootModules.First();
            await rootModule.RunAsync(host, args, default);
        }
        else
        {
            // No root modules found, show the default Avalonia UI
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static async Task ListModulesAsync()
    {
        var host = await GetOrCreateHostAsync();
        
        Console.WriteLine("Installed Modules:");
        Console.WriteLine("------------------");
        
        if (!host.All.Any())
        {
            Console.WriteLine("No modules installed.");
            return;
        }

        foreach (var module in host.All)
        {
            var type = module is IRootModule ? "Root Module" : "Module";
            Console.WriteLine($"- {module.Name} ({module.Id}) v{module.Version} [{type}]");
        }

        if (host.Failed.Any())
        {
            Console.WriteLine("\nFailed Modules:");
            Console.WriteLine("---------------");
            foreach (var module in host.Failed)
            {
                Console.WriteLine($"- {module.Name} ({module.Id})");
            }
        }
    }

    public static async Task InstallModuleAsync(string dllPath)
    {
        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"Error: File not found at '{dllPath}'");
            return;
        }

        var modulesPath = Path.Combine(AppContext.BaseDirectory, "modules");
        if (!Directory.Exists(modulesPath))
        {
            Directory.CreateDirectory(modulesPath);
        }

        var fileName = Path.GetFileName(dllPath);
        var destPath = Path.Combine(modulesPath, fileName);

        try
        {
            File.Copy(dllPath, destPath, overwrite: true);
            Console.WriteLine($"Successfully installed module to '{destPath}'");
            
            // Try to load it to verify it works
            var host = await GetOrCreateHostAsync();
            var result = await host.LoadAsync(destPath);
            
            if (result.Success)
            {
                Console.WriteLine($"Module '{result.Module?.Name}' loaded successfully.");
            }
            else
            {
                Console.WriteLine($"Warning: Module copied but failed to load: {result.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error installing module: {ex.Message}");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
