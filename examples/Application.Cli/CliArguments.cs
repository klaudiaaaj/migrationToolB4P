namespace Application.Cli;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options;

    private CliArguments(string command, Dictionary<string, string?> options)
    {
        Command = command;
        _options = options;
    }

    public string Command { get; }

    public static CliArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliArguments(
                "help",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        }

        var command = args[0].Trim().ToLowerInvariant();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Nieoczekiwany argument '{token}'. Opcje muszą zaczynać się od --.");
            }

            var key = token[2..];
            string? value = null;
            if (index + 1 < args.Length &&
                !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            options[key] = value;
        }

        return new CliArguments(command, options);
    }

    public string? Get(string name)
        => _options.TryGetValue(name, out var value) ? value : null;

    public string GetRequired(string name)
        => Get(name) ?? throw new ArgumentException($"Brakuje wymaganej opcji --{name}.");

    public bool HasFlag(string name)
        => _options.ContainsKey(name) && _options[name] is null;
}
