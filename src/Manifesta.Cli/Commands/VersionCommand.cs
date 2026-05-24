using System.CommandLine;
using System.Reflection;

namespace Manifesta.Cli.Commands;

/// <summary>manifesta version — prints version info.</summary>
public sealed class VersionCommand : Command
{
    public VersionCommand() : base("version", "Print version info")
    {
        this.SetHandler(Handle);
    }

    private static void Handle()
    {
        var version = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "1.3.0";

        Console.WriteLine($"manifesta {version} (community edition)");
        Console.WriteLine($"spec:      v1.3");
        Console.WriteLine($"runtime:   {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }
}
