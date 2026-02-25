using System;
using System.IO;
using Tomlyn;
using Wyre.ModuleHost.Integrity;

namespace WyreConnector;

public enum IntegrityFailureReason
{
    IntegrityFileNotFound,
    IntegrityFileCorrupt,
    SignatureMismatch,
    ModuleHostNotFound,
    ModuleHostHashMismatch
}

public record IntegrityResult
{
    public bool Success { get; init; }
    public IntegrityFailureReason? Reason { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }

    public static IntegrityResult Ok() => new() { Success = true };
    public static IntegrityResult Fail(IntegrityFailureReason reason, string? expected = null, string? actual = null) => 
        new() { Success = false, Reason = reason, Expected = expected, Actual = actual };
}

public static class AppIntegrityCheck
{
    public static IntegrityResult ValidateModuleHost()
    {
#if DISABLE_INTEGRITY_CHECKS
        return IntegrityResult.Ok();
#else
        // 1. Load integrity.toml
        var integrityPath = Path.Combine(AppContext.BaseDirectory, "integrity.toml");
        if (!File.Exists(integrityPath))
            return IntegrityResult.Fail(IntegrityFailureReason.IntegrityFileNotFound);
        
        IntegrityManifest manifest;
        try { manifest = Toml.ToModel<IntegrityManifest>(File.ReadAllText(integrityPath)); }
        catch { return IntegrityResult.Fail(IntegrityFailureReason.IntegrityFileCorrupt); }
        
        // 2. Verify signature over entire manifest
        if (!VerifyManifestSignature(manifest))
            return IntegrityResult.Fail(IntegrityFailureReason.SignatureMismatch);
        
        // 3. Validate ModuleHost.dll hash — only this one, nothing else
        var moduleHostPath = Path.Combine(AppContext.BaseDirectory, "ModuleHost.dll");
        if (!File.Exists(moduleHostPath))
            return IntegrityResult.Fail(IntegrityFailureReason.ModuleHostNotFound);
        
        var actualHash = IntegrityValidator.ComputeHash(moduleHostPath);
        if (manifest.Critical.TryGetValue("ModuleHost.dll", out var expectedHash))
        {
            if (actualHash != expectedHash)
                return IntegrityResult.Fail(IntegrityFailureReason.ModuleHostHashMismatch,
                    expected: expectedHash,
                    actual: actualHash);
        }
        else
        {
            return IntegrityResult.Fail(IntegrityFailureReason.ModuleHostHashMismatch,
                expected: "known",
                actual: "unknown");
        }
        
        return IntegrityResult.Ok();
#endif
    }
    
    private static bool VerifyManifestSignature(IntegrityManifest manifest)
    {
        // In a real implementation, we would reconstruct the canonical signing payload
        // and verify the signature using the public key.
        // For this prototype, we'll just return true if the signature is present.
        return !string.IsNullOrEmpty(manifest.Signature.Value);
    }
}
