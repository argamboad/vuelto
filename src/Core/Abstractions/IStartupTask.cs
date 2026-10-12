namespace Vuelto.Core.Abstractions;

/// <summary>
/// Work that runs once at startup, after the schema is migrated and before the first request is served (Arch A1,
/// R159): a curated catalog seed, a backfill the app owns. Register one in <c>AppComposition.AddAppServices</c>;
/// <c>Program.cs</c> runs every registered task in registration order, each in the one startup scope, and stays
/// identical across the platform and its apps. A task decides for itself whether it applies (a relational database,
/// a config switch) and must be idempotent: a boot with nothing to do is a cheap check, never a write.
/// </summary>
public interface IStartupTask
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
