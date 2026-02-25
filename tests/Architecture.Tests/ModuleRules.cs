using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Wyre.Core.Messaging;
using Wyre.ModuleHost;

namespace Architecture.Tests;

public class ModuleRules
{
    private static readonly Assembly CoreAssembly = typeof(IMessageBus).Assembly;
    private static readonly Assembly ModuleHostAssembly = typeof(ModuleHostImpl).Assembly;

    [Fact]
    public void Core_References_Nothing()
    {
        var result = Types.InAssembly(CoreAssembly)
            .Should().NotHaveDependencyOnAny(
                "Wyre.ModuleHost", "Avalonia", "Orleans", "Grpc")
            .GetResult();
            
        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void ModuleHost_References_Only_Core()
    {
        // ModuleHost can reference Core, but not other modules
        // We don't have other modules yet, but we can test it doesn't reference Avalonia
        var result = Types.InAssembly(ModuleHostAssembly)
            .Should().NotHaveDependencyOnAny("Avalonia", "Orleans", "Grpc")
            .GetResult();
            
        result.IsSuccessful.Should().BeTrue();
    }
}
