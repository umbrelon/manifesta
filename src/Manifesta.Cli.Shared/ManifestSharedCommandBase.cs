using System.CommandLine;
using System.CommandLine.Invocation;
using Manifesta.Core;

namespace Manifesta.Cli.Commands;

/// <summary>
/// Minimal base class shared across OSS and enterprise CLI builds.
/// Provides the InvokeBaseAsync lifecycle (pre-hook → execute → post-hook)
/// and the ExecuteAsync contract.  Richer helpers (table/section/API loading)
/// live in the per-edition ManifestCommandBase subclass.
/// </summary>
public abstract class ManifestSharedCommandBase : Command
{
    protected ManifestSharedCommandBase(string name, string description)
        : base(name, description) { }

    protected async Task<int> InvokeBaseAsync(InvocationContext context)
    {
        var globals = GlobalOptionDefinitions.Bind(context.ParseResult);
        var ct      = context.GetCancellationToken();

        if (!string.IsNullOrWhiteSpace(globals.PreHook))
        {
            var ok = await HookRunner.RunAsync(globals.PreHook, globals, ct: ct);
            if (!ok) return (int)ExitCode.ValidationErrors;
        }

        int exitCode;
        try
        {
            exitCode = await ExecuteAsync(globals, context, ct);
        }
        catch (ManifestaSchemException ex)
        {
            OutputFormatter.WriteError(ex.Message);
            return (int)ExitCode.FatalSchemaErrors;
        }
        catch (ManifestaConfigException ex)
        {
            OutputFormatter.WriteError(ex.Message);
            return (int)ExitCode.ConfigOrInvocationError;
        }
        catch (ManifestaReleaseException ex)
        {
            OutputFormatter.WriteError(ex.Message);
            return (int)ExitCode.ReleaseRepoFailure;
        }
        catch (Exception ex)
        {
            OutputFormatter.WriteError($"Unexpected error: {ex.Message}");
            if (globals.Verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return (int)ExitCode.InternalError;
        }

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(globals.PostHook))
        {
            var ok = await HookRunner.RunAsync(globals.PostHook, globals, ct: ct);
            if (!ok) return (int)ExitCode.ValidationErrors;
        }

        return exitCode;
    }

    protected abstract Task<int> ExecuteAsync(
        GlobalOptions     globals,
        InvocationContext context,
        CancellationToken ct);
}
