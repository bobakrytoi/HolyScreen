using System.Collections.Generic;

namespace HolyScreen.Core;

/// <summary>Parsed command-line options. Unknown arguments are ignored so shortcuts stay forward-compatible.</summary>
internal sealed class CommandLineArgs
{
    public string? DataDirectory { get; private set; }

    /// <summary>True when launched by the Windows "Run" entry.</summary>
    public bool LaunchedAtStartup { get; private set; }

    /// <summary>A command for the running instance (or for this one, once it has started).</summary>
    public InstanceCommand? Command { get; private set; }

    public static CommandLineArgs Parse(IReadOnlyList<string> args)
    {
        var result = new CommandLineArgs();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--startup":
                    result.LaunchedAtStartup = true;
                    break;
                case "--data-dir":
                case "--data-directory":
                    if (i + 1 < args.Count) result.DataDirectory = args[++i];
                    break;
                case "--capture":
                    result.Command = InstanceCommand.CaptureRegion;
                    break;
                case "--capture-full":
                    result.Command = InstanceCommand.CaptureFullScreen;
                    break;
                case "--settings":
                    result.Command = InstanceCommand.Settings;
                    break;
                case "--history":
                    result.Command = InstanceCommand.History;
                    break;
                case "--capture-delayed":
                    result.Command = InstanceCommand.CaptureDelayed;
                    break;
                case "--exit":
                case "--quit":
                    result.Command = InstanceCommand.Exit;
                    break;
            }
        }

        return result;
    }
}
