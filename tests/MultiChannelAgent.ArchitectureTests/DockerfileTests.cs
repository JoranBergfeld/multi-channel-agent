using System.Text.RegularExpressions;

namespace MultiChannelAgent.ArchitectureTests;

/// <summary>
/// Static guard against the class of bug where the single deployable container image simply cannot
/// build: a <c>COPY</c> instruction in the repository-root <c>Dockerfile</c> references a source path
/// that does not exist in the build context. This is deliberately generic - it parses every
/// non-multi-stage <c>COPY</c> instruction and checks every source path against the filesystem -
/// rather than special-casing today's specific stale filename, so it also catches future drift (a
/// renamed/moved project file, a deleted script, etc.) the same way. Multi-stage
/// <c>COPY --from=&lt;stage&gt;</c> instructions are skipped: their sources live in a previous build
/// stage's filesystem, not the repository build context, so they are outside what this check can (or
/// should) validate.
/// </summary>
public class DockerfileTests
{
    [Fact]
    public void Every_Dockerfile_COPY_instruction_references_a_source_path_that_exists_in_the_repository()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dockerfilePath = Path.Combine(repositoryRoot, "Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Expected to find a Dockerfile at {dockerfilePath}.");

        var missingSources = new List<string>();

        foreach (var sourcePath in ParseCopySourcePaths(File.ReadAllLines(dockerfilePath)))
        {
            var resolvedPath = Path.Combine(repositoryRoot, sourcePath);
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                missingSources.Add(sourcePath);
            }
        }

        Assert.True(
            missingSources.Count == 0,
            "Dockerfile COPY instruction(s) reference source path(s) that do not exist in the " +
            $"repository build context: {string.Join(", ", missingSources)}");
    }

    /// <summary>
    /// Extracts the source path(s) named by every plain (non <c>--from=</c>) <c>COPY</c> instruction
    /// in a Dockerfile. A <c>COPY</c> instruction's last whitespace-separated token is always its
    /// destination; every token before it is a source path relative to the build context.
    /// </summary>
    private static IEnumerable<string> ParseCopySourcePaths(IEnumerable<string> dockerfileLines)
    {
        foreach (var rawLine in dockerfileLines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = Regex.Split(line, @"\s+").Skip(1).ToList();

            // Multi-stage copies (COPY --from=<stage> ...) pull from a previous build stage's
            // filesystem, not the repository build context, so they are not checked here.
            if (tokens.Any(t => t.StartsWith("--from=", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Drop any other flags (e.g. --chown=...) - they never carry a build-context path.
            var pathTokens = tokens.Where(t => !t.StartsWith("--", StringComparison.OrdinalIgnoreCase)).ToList();

            // The last remaining token is always the destination inside the image; everything
            // before it is a source path in the build context.
            for (var i = 0; i < pathTokens.Count - 1; i++)
            {
                yield return pathTokens[i];
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MultiChannelAgent.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root (a directory containing MultiChannelAgent.slnx) starting from {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
