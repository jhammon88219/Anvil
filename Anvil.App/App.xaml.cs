using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class. Owns the DI
	/// container (composition root): every service/provider/VM is registered here and the object graph is
	/// resolved at launch, replacing the hand-wired `new`s that used to live in MainWindow's constructor.
	/// </summary>
	public partial class App : Application
	{
		private Window? _window;

		/// <summary>The app-wide dependency-injection container. Built once at launch (see OnLaunched).</summary>
		public IServiceProvider Services { get; private set; } = null!;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			InitializeComponent();
		}

		/// <summary>
		/// Invoked when the application is launched.
		/// </summary>
		protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			// Build the container and resolve the whole object graph HERE, on the UI thread: WinUiDispatcher
			// (pulled in as part of the MainWindow graph) captures the CURRENT thread's DispatcherQueue in
			// its constructor, and OnLaunched runs on the UI thread. Resolving MainWindow constructs
			// everything synchronously on this thread.
			Services = ConfigureServices();
			_window = Services.GetRequiredService<MainWindow>();
			_window.Activate();
		}

		private static IServiceProvider ConfigureServices()
		{
			var services = new ServiceCollection();

			// ── Providers + data services (leaf singletons; each owns its own on-disk cache / config). ──
			services.AddSingleton<IStyleProvider, StyleProvider>();
			services.AddSingleton<IRegionProvider, RegionProvider>();
			services.AddSingleton<ISpcOutlookService, SpcOutlookService>();
			services.AddSingleton<ISpcWatchService, SpcWatchService>();
			services.AddSingleton<IWarningService, WarningService>();
			services.AddSingleton<IStormReportService, StormReportService>();
			services.AddSingleton<ILevel2RadarService, Level2RadarService>();
			services.AddSingleton<IRadarSiteProvider, RadarSiteProvider>();
			services.AddSingleton<ILocationService, LocationService>();
			services.AddSingleton<IDowEventProvider, DowEventProvider>();
			services.AddSingleton<ISettingsService, SettingsService>();

			// ── UI-thread marshaller seam (Core interface, WinUI impl). Resolved on the UI thread. ──
			services.AddSingleton<IDispatcher, WinUiDispatcher>();

			// ── Map command bus. Registered concretely + aliased to IMapService so MainWindow can Attach
			//    itself as the IMapView after construction (breaking the MainWindow↔MapService ctor cycle),
			//    while the view models depend only on IMapService. ──
			services.AddSingleton<MapService>();
			services.AddSingleton<IMapService>(sp => sp.GetRequiredService<MapService>());

			// ── View models + the JS→C# router + the window (the composition root). ──
			services.AddSingleton<MapViewModel>();
			services.AddSingleton<WebMessageRouter>();
			services.AddSingleton<MainWindow>();

			return services.BuildServiceProvider();
		}
	}
}
