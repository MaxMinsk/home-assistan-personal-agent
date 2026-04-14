using System.Text;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: консервативная safety policy для Home Assistant MCP tools.
/// Зачем: до confirmation layer агенту нельзя отдавать tools, которые могут изменить состояние дома.
/// Как: классифицирует tool по имени и описанию; read-only отдает агенту, все сомнительное требует будущего confirmation.
/// </summary>
public sealed class HomeAssistantMcpToolPolicy
{
    private static readonly string[] ReadOnlyNameMarkers =
    [
        "get",
        "read",
        "list",
        "search",
        "query",
        "state",
        "history",
        "status",
    ];

    private static readonly string[] UnsafeNameMarkers =
    [
        "turnon",
        "turnoff",
        "toggle",
        "set",
        "update",
        "delete",
        "create",
        "remove",
        "callservice",
        "service",
        "execute",
        "run",
        "write",
        "control",
        "lock",
        "unlock",
        "open",
        "close",
        "press",
        "trigger",
        "activate",
    ];

    private static readonly string[] UnsafeDescriptionMarkers =
    [
        "turn on",
        "turn off",
        "toggle",
        "set ",
        "call service",
        "execute",
        "run script",
        "control",
        "lock",
        "unlock",
        "open or close",
        "write",
        "delete",
        "create",
        "update",
        "remove",
    ];

    public HomeAssistantMcpToolSafety Classify(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalizedName = NormalizeIdentifier(name);
        if (UnsafeNameMarkers.Any(normalizedName.Contains))
        {
            return HomeAssistantMcpToolSafety.RequiresConfirmation;
        }

        var normalizedDescription = description?.Trim().ToLowerInvariant() ?? string.Empty;
        if (UnsafeDescriptionMarkers.Any(normalizedDescription.Contains))
        {
            return HomeAssistantMcpToolSafety.RequiresConfirmation;
        }

        return ReadOnlyNameMarkers.Any(normalizedName.Contains)
            ? HomeAssistantMcpToolSafety.ReadOnly
            : HomeAssistantMcpToolSafety.RequiresConfirmation;
    }

    private static string NormalizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
