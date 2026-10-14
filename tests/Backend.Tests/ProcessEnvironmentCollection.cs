namespace Backend.Tests;

/// <summary>
/// HTTP fixtures configure the API through process-wide environment variables before building the host. Run them one
/// at a time: in parallel, one fixture can build its host with another fixture's connection string and both migrate
/// the same database.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
