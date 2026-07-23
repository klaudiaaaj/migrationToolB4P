using System.Diagnostics;

namespace MigrationTool.Core.Git;

public sealed class GitClient
{
    private readonly string _repositoryRoot;

    public GitClient(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        EnsureRepository();
    }

    public string RepositoryRoot => _repositoryRoot;

    public IReadOnlyList<string> ListFiles(string gitRef, string relativeRoot)
    {
        var normalizedRoot = NormalizeGitPath(relativeRoot).TrimEnd('/');
        var output = Run("ls-tree", "-r", "--name-only", gitRef, "--", normalizedRoot);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeGitPath)
            .ToArray();
    }

    public string ReadFile(string gitRef, string relativePath)
    {
        var objectName = $"{gitRef}:{NormalizeGitPath(relativePath)}";
        return Run("show", objectName);
    }

    public bool RefExists(string gitRef)
    {
        try
        {
            Run("rev-parse", "--verify", gitRef);
            return true;
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    public string FindRepositoryRoot()
        => Run("rev-parse", "--show-toplevel").Trim();

    private void EnsureRepository()
    {
        try
        {
            Run("rev-parse", "--is-inside-work-tree");
        }
        catch (GitCommandException exception)
        {
            throw new InvalidOperationException(
                $"Katalog '{_repositoryRoot}' nie jest repozytorium Git.", exception);
        }
    }

    private string Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Nie udało się uruchomić procesu git.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new GitCommandException(
                $"Polecenie git {string.Join(" ", arguments)} zakończyło się kodem {process.ExitCode}: " +
                standardError.Trim());
        }

        return standardOutput;
    }

    private static string NormalizeGitPath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}

public sealed class GitCommandException : Exception
{
    public GitCommandException(string message) : base(message)
    {
    }
}
