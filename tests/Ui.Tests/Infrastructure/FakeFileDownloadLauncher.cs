using Vuelto.Shared.Ui;

namespace Vuelto.Ui.Tests.Infrastructure;

/// <summary>Records every download handed to the host launcher (REPORTS-2 export, household export) instead of navigating or sharing.</summary>
public sealed class FakeFileDownloadLauncher : IFileDownloadLauncher
{
    public List<(string Url, string FallbackFileName)> Launched { get; } = [];

    public Task LaunchAsync(string url, string fallbackFileName)
    {
        Launched.Add((url, fallbackFileName));
        return Task.CompletedTask;
    }
}
