namespace MigrationTool.Core.Domain;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationMessage(
    ValidationSeverity Severity,
    string Code,
    string Message);

public sealed class ValidationResult
{
    private readonly List<ValidationMessage> _messages = [];

    public IReadOnlyList<ValidationMessage> Messages => _messages;
    public bool IsValid => _messages.All(x => x.Severity != ValidationSeverity.Error);

    public void Error(string code, string message)
        => _messages.Add(new ValidationMessage(ValidationSeverity.Error, code, message));

    public void Warning(string code, string message)
        => _messages.Add(new ValidationMessage(ValidationSeverity.Warning, code, message));

    public void Merge(ValidationResult other)
        => _messages.AddRange(other.Messages);
}
