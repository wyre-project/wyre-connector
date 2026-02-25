using System.Security.Cryptography;

namespace Wyre.ModuleHost.Integrity;

public record IntegrityManifest
{
    public Dictionary<string, string> Critical { get; init; } = new();
    public Dictionary<string, string> Core { get; init; } = new();
    public Dictionary<string, string> Builtin { get; init; } = new();
    public Dictionary<string, ExternalHashEntry> External { get; init; } = new();
    public SignatureInfo Signature { get; set; } = new();
}

public record ExternalHashEntry
{
    public string Hash { get; init; } = "";
    public DateTimeOffset ApprovedAt { get; init; }
    public bool ApprovedByUser { get; init; }
}

public record SignatureInfo
{
    public string PublicKey { get; init; } = "";
    public DateTimeOffset SignedAt { get; init; }
    public string Value { get; init; } = "";
}

public record DiscoveredModule(string Path, string Filename, string Name);

public record ModuleValidationResult
{
    public bool ShouldLoad { get; init; }
    public bool IsWarning { get; init; }
    public string? ActualHash { get; init; }
    public bool AbortLaunch { get; init; }

    public static ModuleValidationResult Approved() => new() { ShouldLoad = true };
    public static ModuleValidationResult ApprovedWithWarning(string hash) => new() { ShouldLoad = true, IsWarning = true, ActualHash = hash };
    public static ModuleValidationResult Skipped() => new() { ShouldLoad = false };
    public static ModuleValidationResult ApprovedOnce() => new() { ShouldLoad = true };
    public static ModuleValidationResult Abort() => new() { ShouldLoad = false, AbortLaunch = true };
}

public record ValidationPlan
{
    public bool HasHardFailure { get; private set; }
    public bool ShouldAbortLaunch { get; private set; }
    public List<(DiscoveredModule Module, ModuleValidationResult Result)> Entries { get; } = new();
    public string? HardFailureMessage { get; private set; }

    public void AddHardFailure(string filename, string expected, string actual, string message)
    {
        HasHardFailure = true;
        HardFailureMessage = $"{message}\nExpected: {expected}\nActual: {actual}";
    }

    public void Add(DiscoveredModule module, ModuleValidationResult result)
    {
        Entries.Add((module, result));
        if (result.AbortLaunch)
        {
            ShouldAbortLaunch = true;
        }
    }
}

public interface IUserPrompt
{
    Task<string> AskAsync(ModuleIntegrityPrompt prompt);
}

public record ModuleIntegrityPrompt
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string[] Options { get; init; } = Array.Empty<string>();
}

public interface IHashStore
{
    Task UpdateExternalAsync(string filename, string hash);
    Task StoreExternalAsync(string filename, ExternalHashEntry entry);
}

public class IntegrityValidator
{
    private readonly IntegrityManifest _manifest;
    private readonly IUserPrompt _prompt;
    private readonly IHashStore _hashStore;

    public IntegrityValidator(IntegrityManifest manifest, IUserPrompt prompt, IHashStore hashStore)
    {
        _manifest = manifest;
        _prompt = prompt;
        _hashStore = hashStore;
    }

    public async Task<ValidationPlan> ValidateAllAsync(IReadOnlyList<DiscoveredModule> discovered)
    {
        var plan = new ValidationPlan();

#if DISABLE_INTEGRITY_CHECKS
        foreach (var module in discovered)
        {
            plan.Add(module, ModuleValidationResult.Approved());
        }
        return plan;
#else
        // Core.dll — hard error, no user choice
        await ValidateCriticalAsync("Core.dll", plan);
        if (plan.HasHardFailure) return plan;

        foreach (var module in discovered)
        {
            var result = await ValidateModuleAsync(module);
            plan.Add(module, result);
            if (result.AbortLaunch) break;
        }

        return plan;
#endif
    }

    private Task ValidateCriticalAsync(string filename, ValidationPlan plan)
    {
        var path = Path.Combine(AppContext.BaseDirectory, filename);
        if (!File.Exists(path))
        {
            plan.AddHardFailure(filename, "exists", "missing", "Core component missing. Cannot continue.");
            return Task.CompletedTask;
        }

        var actual = ComputeHash(path);
        if (_manifest.Core.TryGetValue(filename, out var expected))
        {
            if (actual != expected)
            {
                plan.AddHardFailure(filename, expected, actual, "Core component hash mismatch. Cannot continue.");
            }
        }
        else
        {
            plan.AddHardFailure(filename, "known", "unknown", "Core component not found in manifest. Cannot continue.");
        }

        return Task.CompletedTask;
    }

    private async Task<ModuleValidationResult> ValidateModuleAsync(DiscoveredModule module)
    {
        var actual = ComputeHash(module.Path);

        // Built-in module
        if (_manifest.Builtin.TryGetValue(module.Filename, out var expectedBuiltin))
        {
            if (actual == expectedBuiltin)
                return ModuleValidationResult.Approved();

            var choice = await _prompt.AskAsync(new ModuleIntegrityPrompt
            {
                Title = "Built-in Module Modified",
                Message = $"{module.Name} has been modified or corrupted.\n\n" +
                          $"Expected: {expectedBuiltin[..Math.Min(24, expectedBuiltin.Length)]}...\n" +
                          $"Found:    {actual[..Math.Min(24, actual.Length)]}...\n\n" +
                          "This module ships with the application. " +
                          "Loading a modified version could be a security risk.",
                Options = new[] { "Load Anyway", "Skip This Module", "Abort Launch" }
            });

            return choice switch
            {
                "Load Anyway" => ModuleValidationResult.ApprovedWithWarning(actual),
                "Skip This Module" => ModuleValidationResult.Skipped(),
                _ => ModuleValidationResult.Abort()
            };
        }

        // External module — check stored hash
        if (_manifest.External.TryGetValue(module.Filename, out var stored))
        {
            if (actual == stored.Hash)
                return ModuleValidationResult.Approved();

            var choice = await _prompt.AskAsync(new ModuleIntegrityPrompt
            {
                Title = "External Module Changed",
                Message = $"{module.Name} has changed since it was last approved.\n\n" +
                          $"Previously: {stored.Hash[..Math.Min(24, stored.Hash.Length)]}...\n" +
                          $"Now:        {actual[..Math.Min(24, actual.Length)]}...\n\n" +
                          $"Last approved: {stored.ApprovedAt:yyyy-MM-dd HH:mm}\n\n" +
                          "This could be a legitimate update or a security risk.",
                Options = new[] { "Approve New Version", "Skip This Module" }
            });

            if (choice == "Approve New Version")
            {
                await _hashStore.UpdateExternalAsync(module.Filename, actual);
                return ModuleValidationResult.Approved();
            }
            return ModuleValidationResult.Skipped();
        }

        // External module — never seen before
        var firstChoice = await _prompt.AskAsync(new ModuleIntegrityPrompt
        {
            Title = "New External Module",
            Message = $"{module.Name} is not a built-in module.\n\n" +
                      $"Location: {module.Path}\n" +
                      $"Hash: {actual[..Math.Min(24, actual.Length)]}...\n\n" +
                      "Do you want to load this module?",
            Options = new[] { "Load and Remember", "Load Once", "Skip" }
        });

        return firstChoice switch
        {
            "Load and Remember" => await ApproveAndStoreAsync(module, actual),
            "Load Once" => ModuleValidationResult.ApprovedOnce(),
            _ => ModuleValidationResult.Skipped()
        };
    }

    private async Task<ModuleValidationResult> ApproveAndStoreAsync(DiscoveredModule module, string hash)
    {
        await _hashStore.StoreExternalAsync(module.Filename, new ExternalHashEntry
        {
            Hash = hash,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedByUser = true
        });
        return ModuleValidationResult.Approved();
    }

    public static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return $"sha256:{Convert.ToHexString(hash).ToLower()}";
    }
}
