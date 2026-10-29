using System.IO;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The temporal SESSION + PANES on <see cref="MapViewModel"/>: which modes were on come back
	/// (MapViewModel.OnMapsReadyAsync calls RestoreTemporalSession; these tests call it directly, since a full map-ready
	/// needs real services), a mode's pane shows WHILE THE MODE RUNS — held back only by the launch order and the loop
	/// holding gate — and nothing the app does while closing is saved as a choice.
	/// </summary>
	/// <remarks>The real MapViewModel over do-nothing services (<see cref="TemporalWindowPersistenceTests.Null{T}"/>).</remarks>
	public class TemporalSessionTests
	{
		private static T N<T>() where T : class => TemporalWindowPersistenceTests.Null<T>.Create();

		private static MapViewModel New(AppSettings settings, IStormReportService? stormReports = null)
		{
			var settingsService = TemporalWindowPersistenceTests.Null<ISettingsService>.Create(
				new() { ["get_Settings"] = _ => settings });
			var usageDir = Path.Combine(Path.GetTempPath(), "AnvilSessionTests", System.Guid.NewGuid().ToString("N"));
			return new MapViewModel(N<IMapService>(), N<IStyleProvider>(), N<IThemeProvider>(), N<IRegionProvider>(),
				N<ISpcOutlookService>(), N<ISpcWatchService>(), N<IWarningService>(), stormReports ?? N<IStormReportService>(),
				N<IDamageSurveyService>(), N<IStormCellService>(), N<IPastAlertService>(), N<IMesoDiscussionService>(), N<ITropicalService>(), N<IRadarSiteProvider>(), N<ILevel2RadarService>(), N<ILocationService>(),
				N<IPlaceSearchService>(), N<IDowEventProvider>(), N<ISavedEventLibrary>(), N<IDispatcher>(),
				settingsService, NullLoggerFactory.Instance, new SiteUsageStore(NullLogger<SiteUsageStore>.Instance, usageDir),
				N<IRadarNwsStatusService>(), N<IRadarUptimeService>(), N<IRadarMessageHistoryService>(),
				N<IRadarScanPatternService>(), new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, usageDir), null);
		}

		[Fact]
		public async Task MapReady_does_not_wait_on_the_storm_report_fetch()
		{
			// 2026-10-10: map-ready AWAITED the launch storm-report fetch (SPC + IEM), so a slow server held the temporal
			// panes and the home radar for up to 22.8 s. A fetch that never answers must not hold the launch.
			var never = new TaskCompletionSource<StormReportResult>();
			var reports = TemporalWindowPersistenceTests.Null<IStormReportService>.Create(
				new() { ["EnsureReportsAsync"] = _ => never.Task });
			var vm = New(new AppSettings { NowCastOn = true }, reports);
			vm.LaunchLoopStarting = () => false;

			var ready = vm.OnMapsReadyAsync();
			Assert.Same(ready, await Task.WhenAny(ready, Task.Delay(5000)));
			await ready;
			Assert.True(vm.IsNowCast);           // the session came back…
			Assert.True(vm.IsNowPaneShown);      // …with its pane, while the reports are still loading
		}

		// ── A PANE FOLLOWS ITS MODE ──

		[Fact]
		public void A_pane_shows_while_its_mode_runs_and_only_then()
		{
			var vm = New(new AppSettings());
			vm.RestoreTemporalSession();
			Assert.False(vm.IsNowPaneShown);

			vm.ToggleTemporalMode(TemporalMode.Now);
			Assert.True(vm.IsNowPaneShown);
			vm.ToggleTemporalMode(TemporalMode.Fore);
			Assert.True(vm.IsNowPaneShown && vm.IsForePaneShown);     // Now and Fore coexist

			vm.ToggleTemporalMode(TemporalMode.Past);                  // Past excludes both
			Assert.True(vm.IsPastPaneShown);
			Assert.False(vm.IsNowPaneShown || vm.IsForePaneShown);

			vm.ToggleTemporalMode(TemporalMode.Past);
			Assert.False(vm.IsPastPaneShown);
			Assert.True(vm.IsPaneShown(TemporalMode.Past) == vm.IsPastPaneShown);
		}

		// ── LAUNCH WINDOWS: the first pane at once, the rest after the home radar's first frame, one at a time
		//    (MapViewModel.ShowLaunchPanesAsync) ──

		private static AppSettings NowAndFore() => new() { StartupResume = false, StartupMode = "nowfore" };

		[Fact]
		public async Task First_launch_pane_shows_at_once_the_second_waits_for_the_home_radars_first_frame()
		{
			var vm = New(NowAndFore());
			vm.LaunchLoopStarting = () => true;
			vm.PaneShownTimeoutMs = 10;
			vm.RestoreTemporalSession(deferPanes: true);
			Assert.True(vm.IsNowCast);                    // the MODES come back at once
			Assert.False(vm.IsNowPaneShown);              // the panes are held

			var showing = vm.ShowLaunchPanesAsync();
			Assert.True(vm.IsNowPaneShown);               // the first doesn't wait on the radar
			await Task.Delay(50);
			Assert.False(vm.IsForePaneShown);             // the second does
			vm.Radar.RaiseFirstFrameShown();
			await showing;
			Assert.True(vm.IsForePaneShown);
		}

		[Fact]
		public async Task Launch_panes_show_anyway_when_no_first_frame_comes()
		{
			var vm = New(NowAndFore());
			vm.LaunchLoopStarting = () => true;
			vm.LaunchPaneFallbackMs = 30;
			vm.PaneShownTimeoutMs = 10;
			vm.RestoreTemporalSession(deferPanes: true);
			await vm.ShowLaunchPanesAsync();
			Assert.True(vm.IsNowPaneShown);
			Assert.True(vm.IsForePaneShown);
		}

		[Fact]
		public async Task Launch_panes_show_one_at_a_time()
		{
			var vm = New(NowAndFore());
			vm.LaunchLoopStarting = () => false;           // nothing to wait for: the first shows at once
			vm.PaneShownTimeoutMs = 10_000;
			vm.RestoreTemporalSession(deferPanes: true);

			var showing = vm.ShowLaunchPanesAsync();
			Assert.True(vm.IsNowPaneShown);
			Assert.False(vm.IsForePaneShown);             // not until Now has drawn
			vm.OnTemporalPaneShown();
			await showing;
			Assert.True(vm.IsForePaneShown);
		}

		[Fact]
		public async Task A_launch_pane_whose_mode_went_off_meanwhile_stays_hidden()
		{
			var vm = New(NowAndFore());
			vm.LaunchLoopStarting = () => true;
			vm.PaneShownTimeoutMs = 10;
			vm.RestoreTemporalSession(deferPanes: true);
			var showing = vm.ShowLaunchPanesAsync();
			vm.IsForeCast = false;                        // switched off before the radar came up
			vm.Radar.RaiseFirstFrameShown();
			await showing;
			Assert.True(vm.IsNowPaneShown);
			Assert.False(vm.IsForePaneShown);
		}

		[Fact]
		public void A_mode_the_user_turns_on_during_the_launch_shows_its_pane_at_once()
		{
			var vm = New(new AppSettings { PastCastOn = true });
			vm.RestoreTemporalSession(deferPanes: true);
			Assert.False(vm.IsPastPaneShown);             // held for its launch turn

			vm.ToggleTemporalMode(TemporalMode.Now);      // the user leaves Past for Now before the launch shows it
			Assert.True(vm.IsNowPaneShown);
		}

		[Fact]
		public void FirstRun_EverythingOff_AndNothingSaved()
		{
			var s = new AppSettings();
			var vm = New(s);
			vm.RestoreTemporalSession();

			Assert.False(vm.IsPastCast || vm.IsNowCast || vm.IsForeCast);
			Assert.False(s.PastCastOn || s.NowCastOn || s.ForeCastOn);
			Assert.False(vm.IsPastPaneShown || vm.IsNowPaneShown || vm.IsForePaneShown);
		}

		[Fact]
		public void Restores_NowAndFore_WithTheirPanes()
		{
			var s = new AppSettings { NowCastOn = true, ForeCastOn = true };
			var vm = New(s);

			Assert.False(vm.IsNowCast);                     // not at construction: a mode drives the map (map-ready restores)
			vm.RestoreTemporalSession();

			Assert.True(vm.IsNowCast);
			Assert.True(vm.IsForeCast);
			Assert.False(vm.IsPastCast);
			Assert.True(vm.IsNowPaneShown && vm.IsForePaneShown);
		}

		[Fact]
		public void Restores_PastCast()
		{
			var s = new AppSettings { PastCastOn = true };
			var vm = New(s);
			vm.RestoreTemporalSession();

			Assert.True(vm.IsPastCast);
			Assert.False(vm.IsNowCast);
			Assert.True(vm.IsPastPaneShown);
		}

		// ── Settings → Startup: a FIXED start instead of the last session ──

		[Fact]
		public void FixedStart_NowCast_IgnoresTheLastSession_AndNeverOverwritesIt()
		{
			var s = new AppSettings
			{
				StartupResume = false, StartupMode = StartupModes.Now,
				PastCastOn = true,                           // the last session: PastCast
			};
			var vm = New(s);
			vm.RestoreTemporalSession();

			Assert.True(vm.IsNowCast);
			Assert.False(vm.IsPastCast);
			Assert.False(vm.IsForeCast);
			Assert.True(vm.IsNowPaneShown);
			Assert.True(s.PastCastOn);                       // switching back to resume still finds PastCast
			Assert.False(s.NowCastOn);
		}

		[Fact]
		public void FixedStart_PastCast_AndMapOnly()
		{
			var past = New(new AppSettings { StartupResume = false, StartupMode = StartupModes.Past });
			past.RestoreTemporalSession();
			Assert.True(past.IsPastCast);
			Assert.True(past.IsPastPaneShown);

			var map = New(new AppSettings { StartupResume = false, StartupMode = StartupModes.MapOnly, NowCastOn = true });
			map.RestoreTemporalSession();
			Assert.False(map.IsPastCast || map.IsNowCast || map.IsForeCast);
		}

		[Fact]
		public void FixedStart_AnUnknownToken_StartsInNowCast()
		{
			var vm = New(new AppSettings { StartupResume = false, StartupMode = "bogus" });
			vm.RestoreTemporalSession();
			Assert.True(vm.IsNowCast);
		}

		[Fact]
		public void UserChanges_AreSaved_ButNotWhileShuttingDown()
		{
			var s = new AppSettings();
			var vm = New(s);
			vm.RestoreTemporalSession();

			vm.ToggleTemporalMode(TemporalMode.Now);
			vm.ToggleTemporalMode(TemporalMode.Fore);
			Assert.True(s.NowCastOn);
			Assert.True(s.ForeCastOn);

			vm.Shutdown();
			vm.ToggleTemporalMode(TemporalMode.Now);        // e.g. the app tearing down
			Assert.True(s.NowCastOn);
		}

		// ── The loop holding gate: every pane steps aside while it's up ──

		[Fact]
		public void LoadingScreen_PutsThePanesAway_AndBringsThemBack()
		{
			var s = new AppSettings { PastCastOn = true };
			var vm = New(s);
			vm.RestoreTemporalSession();

			vm.OnLoadingScreenChanged(true);
			Assert.False(vm.IsPastPaneShown);
			Assert.True(s.PastCastOn);                      // the mode is untouched

			vm.OnLoadingScreenChanged(false);
			Assert.True(vm.IsPastPaneShown);
		}

		[Fact]
		public void LoadingScreen_DoesNotBringBackAPaneWhoseModeStopped()
		{
			var vm = New(new AppSettings { PastCastOn = true });
			vm.RestoreTemporalSession();

			vm.OnLoadingScreenChanged(true);
			vm.ToggleTemporalMode(TemporalMode.Past);       // PastCast off while the screen is up
			vm.OnLoadingScreenChanged(false);
			Assert.False(vm.IsPastPaneShown);
		}
	}
}
