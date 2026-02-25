using System;
using System.Threading.Tasks;

namespace WyreConnector;

class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        bool ignoreIntegrity = args.Contains("--ignore-integrity") || 
                               Environment.GetEnvironmentVariable("WYRE_IGNORE_INTEGRITY") == "1";

        if (!ignoreIntegrity)
        {
            // Validate ModuleHost before touching it
            var integrityResult = AppIntegrityCheck.ValidateModuleHost();
            
            if (!integrityResult.Success)
            {
                AppErrorScreen.Show(integrityResult); // bare fallback, no Avalonia
                return 1;
            }
        }

        if (args.Length == 0)
        {
            await ModuleHostBootstrapper.RunAsync(args);
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "run":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: wyre-connector run <module-id>");
                    return 1;
                }
                await ModuleHostBootstrapper.RunAsync(new[] { "--root", args[1] });
                return 0;

            case "list-modules":
                await ModuleHostBootstrapper.ListModulesAsync();
                return 0;

            case "install-module":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: wyre-connector install-module <path-to-dll>");
                    return 1;
                }
                await ModuleHostBootstrapper.InstallModuleAsync(args[1]);
                return 0;

            case "ui":
                await ModuleHostBootstrapper.RunAsync(Array.Empty<string>());
                return 0;

            case "help":
            case "--help":
            case "-h":
                PrintHelp();
                return 0;

            default:
                // If it's not a known command, maybe it's an argument for the default UI/root module
                await ModuleHostBootstrapper.RunAsync(args);
                return 0;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Wyre Connector - The mesh networking and module platform");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  wyre-connector [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  run <module-id>          Run a specific root module");
        Console.WriteLine("  list-modules             List all installed modules");
        Console.WriteLine("  install-module <path>    Install a module from a DLL path");
        Console.WriteLine("  ui                       Start the Wyre Connector UI (default)");
        Console.WriteLine("  help                     Show this help message");
    }
}
