using System.Diagnostics;

namespace Eva.App;

public sealed class GnomeShortcut
{
    public const string PreferredBinding = "<Primary><Super>space";
    private const string Schema = "org.gnome.settings-daemon.plugins.media-keys";

    public async Task<bool> HasConflictAsync(CancellationToken cancellationToken = default)
    {
        var output = await Run(["get", Schema, "custom-keybindings"], cancellationToken).ConfigureAwait(false);
        foreach (var path in ParsePaths(output))
        {
            var binding = await Run(["get", $"{Schema}.custom-keybinding:{path}", "binding"], cancellationToken)
                .ConfigureAwait(false);
            if (binding.Contains(PreferredBinding, StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/eva/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public async Task InstallAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (await HasConflictAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Ctrl+Super+Space is already assigned; Eva did not overwrite it.");
        }
        var path = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/eva/";
        var existing = await Run(["get", Schema, "custom-keybindings"], cancellationToken).ConfigureAwait(false);
        var paths = ParsePaths(existing).Append(path).Distinct(StringComparer.Ordinal).ToArray();
        await Run(["set", Schema, "custom-keybindings", FormatPaths(paths)], cancellationToken).ConfigureAwait(false);
        var child = $"{Schema}.custom-keybinding:{path}";
        await Run(["set", child, "name", "'Eva recording toggle'"], cancellationToken).ConfigureAwait(false);
        await Run(["set", child, "command", $"'{executablePath.Replace("'", "'\\''", StringComparison.Ordinal)} --toggle-recording'"], cancellationToken).ConfigureAwait(false);
        await Run(["set", child, "binding", $"'{PreferredBinding}'"], cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var existing = await Run(["get", Schema, "custom-keybindings"], cancellationToken).ConfigureAwait(false);
        var paths = ParsePaths(existing)
            .Where(static path => !path.Contains("/eva/", StringComparison.Ordinal))
            .ToArray();
        await Run(["set", Schema, "custom-keybindings", FormatPaths(paths)], cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<string> ParsePaths(string value) =>
        value.Trim().TrimStart('@').Replace("as ", "", StringComparison.Ordinal)
            .Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => item.Trim('\'', '"', ' '))
            .Where(static item => item.Length > 0)
            .ToArray();

    private static string FormatPaths(IEnumerable<string> paths) =>
        "[" + string.Join(", ", paths.Select(static path => $"'{path}'")) + "]";

    private static async Task<string> Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("gsettings")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch gsettings.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error);
        }
        return output;
    }
}
