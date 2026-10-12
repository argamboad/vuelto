using Vuelto.Shared.Ui;

namespace Vuelto.Maui;

public partial class App : Application
{
	private readonly AppResumeNotifier _resumeNotifier;

	public App(AppResumeNotifier resumeNotifier)
	{
		InitializeComponent();
		_resumeNotifier = resumeNotifier;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "¿Y el vuelto?" };
		// External round-trips (billing checkout/portal in the system browser) return here with
		// no navigation — tell the open page to refresh itself (NATIVE-4, parity gap G2). Two
		// hooks because the platforms signal the return differently: Android covers the Activity
		// (onStop) and comes back via Resumed; on desktop the browser only steals focus, so the
		// return is Activated (which also fires on plain refocus — the refresh is one cheap GET,
		// standard refresh-on-focus behavior).
		window.Resumed += (_, _) => _resumeNotifier.Notify();
		window.Activated += (_, _) => _resumeNotifier.Notify();
		return window;
	}
}
