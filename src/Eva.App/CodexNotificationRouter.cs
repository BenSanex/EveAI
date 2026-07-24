using System.Text.Json.Nodes;
using EveEsi.Core;

namespace Eva.App;

public enum CodexNotificationKind
{
    Ignore,
    AgentText,
    Diagnostic,
    TurnCompleted
}

public sealed record RoutedCodexNotification(CodexNotificationKind Kind, string? Text = null);

public static class CodexNotificationRouter
{
    private const int MaxDiagnosticLength = 4000;

    public static RoutedCodexNotification Route(JsonNode message)
    {
        var method = message["method"]?.GetValue<string>() ?? "";
        if (string.Equals(method, "item/agentMessage/delta", StringComparison.Ordinal))
        {
            return new(CodexNotificationKind.AgentText, ReadDelta(message));
        }
        if (string.Equals(method, "turn/completed", StringComparison.Ordinal))
        {
            return new(CodexNotificationKind.TurnCompleted);
        }
        if (method.StartsWith("item/commandExecution/", StringComparison.Ordinal) ||
            method is "item/started" or "item/completed")
        {
            var detail = ReadDelta(message) ?? message["params"]?["item"]?["command"]?.GetValue<string>();
            detail = SecretRedactor.Redact(detail ?? "");
            if (detail.Length > MaxDiagnosticLength)
            {
                detail = detail[..MaxDiagnosticLength] + "…";
            }
            return new(
                CodexNotificationKind.Diagnostic,
                string.IsNullOrWhiteSpace(detail) ? method : $"{method}: {detail}");
        }
        return new(CodexNotificationKind.Ignore);
    }

    private static string? ReadDelta(JsonNode message) =>
        message["params"]?["delta"]?.GetValue<string>();
}
