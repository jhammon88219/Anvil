using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class. Owns the DI
	/// container (composition root) and app-wide logging: every service/provider/VM is registered here and
	/// the object graph is resolved at launch, replacing the hand-wired `new`s that used to live in
	/// MainWindow's constructor. Serilog + the three global exception hooks catch and log crashes.
	/// </summary>
	public partial class App : Application
	{
		private Window? _window;
		private readonly ILogger<App> _logger;

		/// <summary>The app-wide dependency-injection container. Built once at launch (see the ctor).</summary>
		public IServiceProvider Services { get; }

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			InitializeComponent();

			// Logging BEFORE the container: AddSerilog(dispose:true) below bridges the static Log.Logger this
			// configures, so it must exist first.
			ConfigureLogging();
			Services = ConfigureServices();
			_logger = Services.GetRequiredService<ILogger<App>>();

			// Catch-all crash logging: the WinUI UI-thread handler (swallow so a stray UI exception doesn't
			// take the app down), the AppDomain handler (fatal — log only), and unobserved Task exceptions.
			UnhandledException += OnUnhandledException;
			AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			// Flush the async file sink on a clean exit (best-effort; a hard kill may skip it).
			AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

			Log.Information("Anvil started");
		}

		/// <summary>
		/// Invoked when the application is launched.
		/// </summary>
		protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			// Resolve the whole object graph HERE, on the UI thread: WinUiDispatcher (pulled in as part of
			// the MainWindow graph) captures the CURRENT thread's DispatcherQueue in its constructor, and
			// OnLaunched runs on the UI thread. Resolving MainWindow constructs everything synchronously.
			_window = Services.GetRequiredService<MainWindow>();
			_window.Activate();
		}

		// Serilog: async rolling file logs under %LocalAppData%\Anvil\Logs (7 days, 10 MB/file) + the VS
		// Output window. The static Log.Logger is bridged into the container's ILogger<T> by AddSerilog.
		private static void ConfigureLogging()
		{
			var logDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Logs");
			Directory.CreateDirectory(logDirectory);
			var logPath = Path.Combine(logDirectory, "log-.txt");

			// {SourceContext} = the logging category (the ILogger<T>'s type, e.g. Anvil.ViewModels.
			// WatchesViewModel), so each line identifies its source without manual "[SPC]"/"[radar]" prefixes.
			const string template =
				"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

			Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Debug()
				.WriteTo.Debug(outputTemplate: template)
				.WriteTo.Async(a => a.File(
					path: logPath,
					outputTemplate: template,
					rollingInterval: RollingInterval.Day,
					retainedFileCountLimit: 7,
					fileSizeLimitBytes: 10 * 1024 * 1024))
				.CreateLogger();
		}

		private IServiceProvider ConfigureServices()
		{
			var services = new ServiceCollection();

			// ── Logging (Serilog behind Microsoft.Extensions.Logging, so any service/VM can inject ILogger<T>). ──
			services.AddLogging(builder => builder.AddSerilog(dispose: true));

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

			// ── App-wide windows: hosts every over-map panel in its own OS window (multi-monitor).
			//    Reactive host with no ctor deps; MainWindow initializes + registers each window with it. ──
			services.AddSingleton<WindowManager>();

			// ── View models + the JS→C# router + the window (the composition root). ──
			services.AddSingleton<MapViewModel>();
			services.AddSingleton<WebMessageRouter>();
			services.AddSingleton<MainWindow>();

			return services.BuildServiceProvider();
		}

		// UI-thread unhandled exception: log it and SWALLOW (Handled = true) so one stray exception doesn't
		// tear the whole app down.
		private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
		{
			_logger.LogCritical(e.Exception, "Unhandled exception: {Message}", e.Message);
			System.Diagnostics.Debug.WriteLine($"[CRASH] {e.Exception}");
			e.Handled = true;
		}

		// AppDomain-level unhandled exception (background threads): fatal, so log only — the process is going
		// down when IsTerminating is true.
		private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
		{
			var ex = e.ExceptionObject as Exception;
			_logger.LogCritical(ex, "AppDomain unhandled exception. IsTerminating: {IsTerminating}", e.IsTerminating);
			System.Diagnostics.Debug.WriteLine($"[DOMAIN CRASH] {e.ExceptionObject}");
		}

		// A faulted Task whose exception was never observed — log it and mark observed so it doesn't escalate.
		private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
		{
			_logger.LogCritical(e.Exception, "Unobserved task exception.");
			System.Diagnostics.Debug.WriteLine($"[TASK CRASH] {e.Exception}");
			e.SetObserved();
		}
	}
}
