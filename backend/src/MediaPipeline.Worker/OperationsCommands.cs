using System.Text.Json;
using MediaPipeline.Application.Processing;

namespace MediaPipeline.Worker;

/// <summary>
/// Operator commands the worker image can run instead of consuming.
/// <para>
/// Shipping them in the same image as the worker means they run against exactly the
/// configuration and code the pipeline uses - an operator never has to reproduce
/// connection strings by hand during an incident.
/// </para>
/// <para>
/// Usage: <c>MediaPipeline.Worker reconcile [--apply]</c>. Reconciliation defaults to a dry run
/// and never deletes media; the only repair it will perform is abandoning long-stale
/// reservations, which describe uploads that delivered no bytes.
/// </para>
/// </summary>
public static class OperationsCommands
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Commands = ["reconcile", "pipeline"];

    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();

        return args[0].ToLowerInvariant() switch
        {
            "reconcile" => await ReconcileAsync(scope.ServiceProvider, args, cancellationToken).ConfigureAwait(false),
            "pipeline" => await PipelineAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false),
            _ => Usage(),
        };
    }

    private static async Task<int> ReconcileAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var reconciliation = services.GetRequiredService<ReconciliationService>();

        var report = await reconciliation.RunAsync(dryRun: !apply, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(report, Output));

        // A non-zero exit lets a scheduled run alert without anyone reading the output.
        return report.Findings.Count == 0 ? 0 : 2;
    }

    private static async Task<int> PipelineAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var operations = services.GetRequiredService<ProcessingOperationsService>();
        var health = await operations.GetPipelineHealthAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(health, Output));
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            QUIRE worker commands

              reconcile [--apply]   Cross-check PostgreSQL against object storage.
                                    Dry run by default. --apply only abandons stale
                                    reservations; media is never deleted.

              pipeline              Print outbox depth and processing job counts.

            With no command the process runs as the media worker.
            """);

        return 1;
    }
}
