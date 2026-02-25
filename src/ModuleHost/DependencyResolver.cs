using Wyre.ModuleHost.Contracts;

namespace Wyre.ModuleHost.Loader;

public record ModuleManifest(
    string Id,
    string Name,
    Version Version,
    string[] RequiredModules,
    string[] OptionalModules,
    string DllPath
);

public record DependencyResolutionResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ModuleManifest>? SortedModules { get; init; }
    public ModuleLoadFailureReason? FailureReason { get; init; }
    public IReadOnlyList<string>? Cycle { get; init; }
    
    public static DependencyResolutionResult Ok(IReadOnlyList<ModuleManifest> sorted) => 
        new() { Success = true, SortedModules = sorted };
        
    public static DependencyResolutionResult Fail(ModuleLoadFailureReason reason, IReadOnlyList<string>? cycle = null) => 
        new() { Success = false, FailureReason = reason, Cycle = cycle };
}

internal class DependencyResolver
{
    private class GraphNode
    {
        public ModuleManifest Manifest { get; }
        public int InDegree { get; set; }
        public List<GraphNode> Dependents { get; } = new();

        public GraphNode(ModuleManifest manifest)
        {
            Manifest = manifest;
        }
    }

    // Kahn's algorithm — topological sort with cycle detection
    public static DependencyResolutionResult Resolve(IReadOnlyList<ModuleManifest> manifests)
    {
        var graph = BuildGraph(manifests);
        var sorted = new List<ModuleManifest>();
        var queue = new Queue<GraphNode>(graph.Values.Where(n => n.InDegree == 0));
        
        while (queue.TryDequeue(out var node))
        {
            sorted.Add(node.Manifest);
            foreach (var dependent in node.Dependents)
            {
                dependent.InDegree--;
                if (dependent.InDegree == 0)
                    queue.Enqueue(dependent);
            }
        }
        
        // If sorted.Count != manifests.Count, a cycle exists
        if (sorted.Count != manifests.Count)
        {
            var cycle = FindCycle(graph.Values); // identify which modules form the loop
            return DependencyResolutionResult.Fail(
                ModuleLoadFailureReason.CircularDependency,
                cycle);
        }
        
        return DependencyResolutionResult.Ok(sorted);
    }

    private static Dictionary<string, GraphNode> BuildGraph(IReadOnlyList<ModuleManifest> manifests)
    {
        var nodes = manifests.ToDictionary(m => m.Id, m => new GraphNode(m));

        foreach (var manifest in manifests)
        {
            var node = nodes[manifest.Id];
            foreach (var req in manifest.RequiredModules)
            {
                if (nodes.TryGetValue(req, out var reqNode))
                {
                    reqNode.Dependents.Add(node);
                    node.InDegree++;
                }
                // If a required module is missing, we don't fail here.
                // The loader will fail when it tries to load the module and checks dependencies.
            }
        }

        return nodes;
    }

    private static IReadOnlyList<string> FindCycle(IEnumerable<GraphNode> nodes)
    {
        // Simple cycle detection for reporting
        var remaining = nodes.Where(n => n.InDegree > 0).Select(n => n.Manifest.Id).ToList();
        return remaining;
    }
}
