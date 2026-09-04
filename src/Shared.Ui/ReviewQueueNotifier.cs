namespace Vuelto.Shared.Ui;

/// <summary>
/// EMAIL-6: an in-process signal that the review queue changed (a draft was confirmed or discarded), so
/// the header's Review badge re-counts without waiting for the next navigation. Registered as a singleton
/// by each host (Web, MAUI) — the same shape as <see cref="AppResumeNotifier"/>.
/// </summary>
public class ReviewQueueNotifier
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
