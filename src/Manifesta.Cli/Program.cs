using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Manifesta.Cli.Commands;
using Manifesta.Core;
using Manifesta.Providers;

// ─── Register DB provider (MySQL + PostgreSQL only) ────────────────────────
DatabaseIntrospectorRegistry.Register(new OssDatabaseIntrospectorFactory());

// ─── Root command ──────────────────────────────────────────────────────────
var root = new RootCommand("Deterministic schema and API documentation engine.");

Manifesta.Cli.GlobalOptionDefinitions.AddToCommand(root);

// ─── Register subcommands ──────────────────────────────────────────────────
root.AddCommand(new InitCommand());
root.AddCommand(new DbCommand());
root.AddCommand(new DocCommand());
root.AddCommand(new ValidateCommand());
root.AddCommand(new DevCommand());
root.AddCommand(new VersionCommand());

// ─── Pipeline (middleware) ─────────────────────────────────────────────────
var parser = new CommandLineBuilder(root)
    .UseDefaults()
    .UseExceptionHandler((ex, context) =>
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        context.ExitCode = (int)Manifesta.Core.ExitCode.InternalError;
    })
    .Build();

// ─── Invoke ────────────────────────────────────────────────────────────────
return await parser.InvokeAsync(args);
