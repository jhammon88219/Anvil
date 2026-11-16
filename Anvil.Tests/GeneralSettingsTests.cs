using System.Collections.Generic;
using System.IO;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Settings → General, General B (2026-11-16): the Unit set (a preset LOCKS the units; Custom brings back the LAST custom
	/// picks), the ForeCast-only start, and the resets the page's DEFAULT column promises.
	/// ⚠️ These touch UnitFormat.Current (global) through the view model — every test ends on the defaults again.
	/// </summary>
	[Collection(UnitFormatCollection.Name)]
	public class GeneralSettingsTests
	{
		private static T N<T>() where T : class => TemporalPanePersistenceTests.Null<T>.Create();

		private static MapViewModel New(AppSettings settings)
		{
			var settingsService = TemporalPanePersistenceTests.Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var themes = TemporalPanePersistenceTests.Null<IThemeProvider>.Create(new()
			{
				["GetThemes"] = _ => (IReadOnlyList<AppTheme>)System.Array.Empty<AppTheme>(),
			});
			var styles = TemporalPanePersistenceTests.Null<IStyleProvider>.Create(
				new() { ["GetStyles"] = _ => (IReadOnlyList<MapStyle>)System.Array.Empty<MapStyle>() });
			var dir = Path.Combine(Path.GetTempPath(), "AnvilGeneralTests", System.Guid.NewGuid().ToString("N"));
			return new MapViewModel(N<IMapService>(), styles, themes, N<IRegionProvider>(),
				N<ISpcOutlookService>(), N<ISpcWatchService>(), N<IWarningService>(), N<IStormReportService>(),
				N<IDamageSurveyService>(), N<IStormCellService>(), N<IPastAlertService>(), N<IMesoDiscussionService>(), N<ITropicalService>(),
				N<IRadarSiteProvider>(), N<ILevel2RadarService>(), N<ILocationService>(),
				N<IPlaceSearchService>(), N<ISavedEventLibrary>(), N<IDispatcher>(),
				settingsService, NullLoggerFactory.Instance, new SiteUsageStore(NullLogger<SiteUsageStore>.Instance, dir),
				N<IRadarNwsStatusService>(), N<IRadarUptimeService>(), N<IRadarMessageHistoryService>(),
				N<IRadarScanPatternService>(), new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, dir), null);
		}

		private static int SetIndex(string token) => UnitChoices.Set.IndexOf(token);

		[Fact]
		public void A_preset_writes_its_whole_set_and_locks_the_pickers()
		{
			var s = new AppSettings();
			var vm = New(s);
			try
			{
				vm.UnitSetIndex = SetIndex("forecaster");
				Assert.False(vm.AreUnitsCustom);
				Assert.Equal(("utc", "nm", "kt", "iso", "decimal"), (s.TimeZoneChoice, s.DistanceUnits, s.SpeedUnits, s.DateFormat, s.CoordinateFormat));

				vm.UnitSetIndex = SetIndex("metric");
				Assert.Equal(("24h", "local", "km", "kmh", "cm", "dayfirst"),
					(s.ClockFormat, s.TimeZoneChoice, s.DistanceUnits, s.SpeedUnits, s.HailUnits, s.DateFormat));
			}
			finally { vm.ResetUnits(); }
		}

		[Fact]
		public void Custom_brings_back_the_last_custom_picks_after_a_preset()
		{
			var s = new AppSettings();
			var vm = New(s);
			try
			{
				vm.SpeedUnitIndex = UnitChoices.Speed.IndexOf("ms");
				vm.DateFormatIndex = UnitChoices.Date.IndexOf("iso");
				vm.CoordinateFormatIndex = UnitChoices.Coords.IndexOf("dms");

				vm.UnitSetIndex = SetIndex("us");
				Assert.Equal(("mph", "written", "decimal"), (s.SpeedUnits, s.DateFormat, s.CoordinateFormat));

				vm.UnitSetIndex = SetIndex("custom");
				Assert.True(vm.AreUnitsCustom);
				Assert.Equal(("ms", "iso", "dms"), (s.SpeedUnits, s.DateFormat, s.CoordinateFormat));
			}
			finally { vm.ResetUnits(); }
		}

		[Fact]
		public void Custom_picks_survive_a_relaunch_on_a_preset()
		{
			var s = new AppSettings();
			var vm = New(s);
			try
			{
				vm.HailUnitIndex = UnitChoices.Hail.IndexOf("cm");
				vm.UnitSetIndex = SetIndex("forecaster");

				var next = New(s); // the next launch, still on Forecaster
				next.UnitSetIndex = SetIndex("custom");
				Assert.Equal("cm", s.HailUnits);
			}
			finally { vm.ResetUnits(); }
		}

		[Fact]
		public void Units_restore_defaults_matches_the_DEFAULT_column()
		{
			var s = new AppSettings();
			var vm = New(s);
			vm.UnitSetIndex = SetIndex("forecaster");
			vm.ResetUnits();

			Assert.True(vm.AreUnitsCustom);
			Assert.Equal(vm.DefaultUnitSetText, vm.UnitSetLabels[vm.UnitSetIndex]);
			Assert.Equal(vm.DefaultDateText, vm.DateFormatLabels[vm.DateFormatIndex]);
			Assert.Equal(vm.DefaultCoordinateText, vm.CoordinateFormatLabels[vm.CoordinateFormatIndex]);
			Assert.Equal(vm.DefaultDistanceText, vm.DistanceUnitLabels[vm.DistanceUnitIndex]);
		}

		[Fact]
		public void Startup_restore_defaults_matches_a_first_run()
		{
			var s = new AppSettings { StartupResume = false, StartupMode = StartupModes.Past, StartInFullScreen = true, LoadHomeOnLaunch = true };
			var vm = New(s);
			vm.ResetStartup();

			var fresh = new AppSettings();
			Assert.Equal((fresh.StartupResume, fresh.StartupMode, fresh.StartInFullScreen, fresh.LoadHomeOnLaunch),
				(s.StartupResume, s.StartupMode, s.StartInFullScreen, s.LoadHomeOnLaunch));
			Assert.Equal(vm.DefaultStartInFullScreenText, s.StartInFullScreen ? "On" : "Off");
		}

		// docs/bugs.md S33: Map only + Load home site on launch brought NowCast and its pane up — the home site's live loop
		// arms NowCast. The launch load is held unless the start has NowCast.
		[Theory]
		[InlineData(StartupModes.MapOnly, true)]
		[InlineData(StartupModes.Fore, true)]
		[InlineData(StartupModes.Now, false)]
		[InlineData(StartupModes.NowFore, false)]
		public void The_home_site_loads_at_launch_only_into_a_NowCast_start(string mode, bool held)
		{
			var vm = New(new AppSettings { StartupResume = false, StartupMode = mode, LoadHomeOnLaunch = true, HomeSiteId = "KTLX" });
			vm.RestoreTemporalSession();
			Assert.Equal(held, vm.SiteFavorites.HoldLaunchLoad!());
		}

		[Fact]
		public void ForeCast_only_is_a_fixed_start_of_its_own()
		{
			Assert.Equal("ForeCast", StartupModes.Labels[StartupModes.IndexOf(StartupModes.Fore)]);
			var vm = New(new AppSettings { StartupResume = false, StartupMode = StartupModes.Fore });
			vm.RestoreTemporalSession();
			Assert.True(vm.IsForeCast);
			Assert.False(vm.IsNowCast);
			Assert.False(vm.IsPastCast);
		}
	}
}
