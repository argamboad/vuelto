namespace Vuelto.Maui;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
	}

	// Android hardware back (NATIVE-4, parity gap G3): walk the WebView's history — which
	// includes Blazor's pushState route changes — instead of the MAUI default (background/exit
	// the Activity on every press). At the history root, fall through to the default so back
	// still leaves the app. No-op on other platforms (they have no hardware back button).
	protected override bool OnBackButtonPressed()
	{
#if ANDROID
		if (blazorWebView.Handler?.PlatformView is Android.Webkit.WebView webView && webView.CanGoBack())
		{
			webView.GoBack();
			return true;
		}
#endif
		return base.OnBackButtonPressed();
	}
}
