namespace OIRF.LanguageServer.Workspace;

/// <summary>
/// Finds the .sln/.csproj to open for a given workspace folder. Handles three cases: the
/// workspace root is a downstream game repo (its own .sln at the root, possibly alongside a
/// nested engine .sln inside a submodule), the workspace root is the engine repo itself
/// (only the engine .sln exists), or an arbitrary subfolder with no .sln at all (falls back to
/// loose .csproj discovery).
/// </summary>
public static class EngineWorkspaceLocator
{
    private static readonly string[] ExcludedDirNames = ["bin", "obj", "node_modules", ".git"];
    private static readonly string[] EngineProjectNames = ["Engine.Shared", "Engine.Client", "Engine.Server"];

    private const int SolutionSearchMaxDepth = 3;
    private const int LooseProjectSearchMaxDepth = 2;

    public sealed record EntryPoint(string? SolutionPath, IReadOnlyList<string> LooseProjectPaths)
    {
        public bool IsEmpty => SolutionPath is null && LooseProjectPaths.Count == 0;
    }

    public static EntryPoint Find(string workspaceRootPath)
    {
        if (!Directory.Exists(workspaceRootPath))
            return new EntryPoint(null, []);

        var solutionCandidates = FindFiles(workspaceRootPath, "*.sln", SolutionSearchMaxDepth);
        if (solutionCandidates.Count > 0)
        {
            var best = solutionCandidates
                .OrderBy(p => PathDepth(workspaceRootPath, p))
                .ThenByDescending(CountEngineReferences)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .First();
            return new EntryPoint(best, []);
        }

        var looseProjects = FindFiles(workspaceRootPath, "*.csproj", LooseProjectSearchMaxDepth);
        return new EntryPoint(null, looseProjects);
    }

    private static List<string> FindFiles(string root, string searchPattern, int maxDepth)
    {
        var results = new List<string>();

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth)
                return;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception) when (depth > 0)
            {
                // permission errors etc. on subfolders are not fatal to the overall search
                return;
            }

            results.AddRange(files);

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(dir);
            }
            catch (Exception) when (depth > 0)
            {
                return;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (ExcludedDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                Walk(subdirectory, depth + 1);
            }
        }

        Walk(root, 0);
        return results;
    }

    private static int PathDepth(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        return relative.Count(c => c is '/' or '\\');
    }

    /// <summary>
    /// Cheap ranking signal: a .sln's own text already lists every referenced project's name and
    /// relative path (e.g. `Project("{...}") = "Engine.Client", "Engine\Engine.Client\...csproj", ...`),
    /// so a substring count over the raw .sln text is enough to prefer the solution that actually
    /// wires up the engine, without needing to parse every referenced .csproj at ranking time.
    /// </summary>
    private static int CountEngineReferences(string solutionPath)
    {
        try
        {
            var text = File.ReadAllText(solutionPath);
            return EngineProjectNames.Count(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
