using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The temporal SESSION on <see cref="MapViewModel"/>: which modes were on and whether their windows
	/// were open come back (MapViewModel.OnMapsReadyAsync calls RestoreTemporalSession; these tests call it
	/// directly, since a full map-ready needs real services), a window never comes back without its mode, pin + lock come back at
	/// construction, and nothing the app does while closing is saved as a choice.
	/// </summary>
	/// <remarks>The real MapViewModel over do-nothing services (<see cref="TemporalWindowPersistenceTests.Null{T}"/>).</remarks>
	public class TemporalSessionTests
	{
		private static T N<T>() where T : class => TemporalWindowPersistenceTests.Null<T>.Create();

		private static MapViewModel New(AppSettings settings)
		{
			var settingsService = TemporalWindowPersistenceTests.Null<ISettingsService>.Create(
				new() { ["get_Settings"] = _ => settings });
			var usageDir = Path.Combine(Path.GetTempPath(), "AnvilSessionTests", System.Guid.NewGuid().ToString("N"));
			return new MapViewModel(N<IMapService>(), N<IStyleProvider>(), N<IThemeProvider>(), N<IRegionProvider>(),
				N<ISpcOutlookService>(), N<ISpcWatchService>(), N<IWarningService>(), N<IStormReportService>(),
				N<IDamageSurveyService>(), N<IStormCellService>(), N<IRadarSiteProvider>(), N<ILevel2RadarService>(), N<ILocationService>(),
				N<IPlaceSearchService>(), N<IDowEventProvider>(), N<ISavedEventLibrary>(), N<IDispatcher>(),
				settingsService, NullLoggerFactory.Instance, new SiteUsageStore(NullLogger<SiteUsageStore>.Instance, usageDir),
				N<IRadarNwsStatusService>(), N<IRadarUptimeService>(), N<IRadarMessageHistoryService>(),
				N<IRadarScanPatternService>(), new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, usageDir), null);
		}

		[Fact]
		public void FirstRun_EverythingOff_AndNothingSaved()
		{
			var s = new AppSettings();
			var vm = New(s);
			vm.RestoreTemporalSession();

			Assert.False(vm.IsPastCast || vm.IsNowCast || vm.IsForeCast);
			Assert.False(s.PastCastOn || s.NowCastOn || s.ForeCastOn);
			Assert.Null(s.NowWindowOnTop);
		}

		[Fact]
		public void Restores_NowAndFore_WithOnlyTheWindowThatWasOpen()
		{
			var s = new AppSettings { NowCastOn = true, ForeCastOn = true, NowWindowOpen = true, ForeWindowOpen = false };
			var vm = New(s);

			Assert.False(vm.IsNowCast);                     // not at construction: a mode drives the map (map-ready restores)
			vm.RestoreTemporalSession();

			Assert.True(vm.IsNowCast);
			Assert.True(vm.IsForeCast);
			Assert.False(vm.IsPastCast);
			Assert.True(vm.IsNowWindowOpen);
			Assert.False(vm.IsForeWindowOpen);              // the mode came back, its window did not
		}

		[Fact]
		public void Restores_PastCast()
		{
			var s = new AppSettings { PastCastOn = true, PastWindowOpen = true };
			var vm = New(s);
			vm.RestoreTemporalSession();

			Assert.True(vm.IsPastCast);
			Assert.False(vm.IsNowCast);
			Assert.True(vm.IsPastWindowOpen);
		}

		[Fact]
		public void AWindow_NeverComesBack_WithoutItsMode()
		{
			var s = new AppSettings { NowCastOn = false, NowWindowOpen = true };
			var vm = New(s);
			vm.RestoreTemporalSession();

			Assert.False(vm.IsNowWindowOpen);
		}

		[Fact]
		public void UserChanges_AreSaved_ButNotWhileShuttingDown()
		{
			var s = new AppSettings();
			var vm = New(s);
			vm.RestoreTemporalSession();

			vm.ToggleTemporalMode(TemporalMode.Now);        // on, which also opens its window
			Assert.True(s.NowCastOn);
			Assert.True(s.NowWindowOpen);

			vm.ToggleTemporalMode(TemporalMode.Fore);
			vm.IsForeWindowOpen = false;
			Assert.True(s.ForeCastOn);
			Assert.False(s.ForeWindowOpen);

			vm.Shutdown();
			vm.IsNowWindowOpen = false;                     // e.g. the app tearing its windows down
			vm.ToggleTemporalMode(TemporalMode.Now);
			Assert.True(s.NowCastOn);
			Assert.True(s.NowWindowOpen);
		}

		[Fact]
		public void PinAndLock_RestoreAtConstruction_AndSave()
		{
			var s = new AppSettings { PastWindowOnTop = false, ForeWindowLocked = false };
			var vm = New(s);

			Assert.False(vm.IsPastWindowOnTop);
			Assert.False(vm.IsForeWindowLocked);
			Assert.True(vm.IsNowWindowOnTop);               // never set → house default

			vm.IsNowWindowLocked = false;
			Assert.False(s.NowWindowLocked);
		}
	}
}
