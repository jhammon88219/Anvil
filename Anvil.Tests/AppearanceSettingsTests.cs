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
	/// Settings → Appearance (2026-11-16): the Basemap pick is PERSISTED ("" = follow the theme), a new theme brings its
	/// own basemap, and the retired Edges page opens Appearance.
	/// </summary>
	[Collection(UnitFormatCollection.Name)] // the Units default-column test resets the GLOBAL units
	public class AppearanceSettingsTests
	{
		private static T N<T>() where T : class => TemporalPanePersistenceTests.Null<T>.Create();

		private static readonly MapStyle Black = new("dataVizBlack", "Data Viz Black", "black.json", "https://mapassets/black.json");
		private static readonly MapStyle Light = new("dataVizlight", "Data Viz Light", "light.json", "https://mapassets/light.json");
		private static readonly MapStyle Grey = new("grey", "Grey", "grey.json", "https://mapassets/grey.json");
		private static readonly AppTheme Dark = new("anvil-dark", "Anvil Dark", ThemeBase.Dark, "dataVizBlack", "#000000");
		private static readonly AppTheme Day = new("anvil-light", "Anvil Light", ThemeBase.Light, "dataVizlight", "#ffffff");

		private static MapViewModel New(AppSettings settings)
		{
			var settingsService = TemporalPanePersistenceTests.Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var styles = TemporalPanePersistenceTests.Null<IStyleProvider>.Create(
				new() { ["GetStyles"] = _ => (IReadOnlyList<MapStyle>)new[] { Black, Light, Grey } });
			var themes = TemporalPanePersistenceTests.Null<IThemeProvider>.Create(new()
			{
				["GetThemes"] = _ => (IReadOnlyList<AppTheme>)new[] { Dark, Day },
				["Resolve"] = a => (string?)a[0] == Day.Id ? Day : Dark,
				["get_Default"] = _ => Dark,
			});
			var dir = Path.Combine(Path.GetTempPath(), "AnvilAppearanceTests", System.Guid.NewGuid().ToString("N"));
			return new MapViewModel(N<IMapService>(), styles, themes, N<IRegionProvider>(),
				N<ISpcOutlookService>(), N<ISpcWatchService>(), N<IWarningService>(), N<IStormReportService>(),
				N<IDamageSurveyService>(), N<IStormCellService>(), N<IPastAlertService>(), N<IMesoDiscussionService>(), N<ITropicalService>(),
				N<IRadarSiteProvider>(), N<ILevel2RadarService>(), N<ILocationService>(),
				N<IPlaceSearchService>(), N<ISavedEventLibrary>(), N<IDispatcher>(),
				settingsService, NullLoggerFactory.Instance, new SiteUsageStore(NullLogger<SiteUsageStore>.Instance, dir),
				N<IRadarNwsStatusService>(), N<IRadarUptimeService>(), N<IRadarMessageHistoryService>(),
				N<IRadarScanPatternService>(), new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, dir), null);
		}

		[Fact]
		public void A_picked_basemap_is_saved_and_comes_back_next_launch()
		{
			var settings = new AppSettings { ThemeId = Dark.Id };
			New(settings).SelectedStyle = Grey;
			Assert.Equal("grey", settings.BasemapStyleId);

			Assert.Same(Grey, New(settings).SelectedStyle); // the next launch
		}

		[Fact]
		public void The_themes_own_basemap_is_saved_as_follow_the_theme()
		{
			var settings = new AppSettings { ThemeId = Dark.Id, BasemapStyleId = "grey" };
			New(settings).SelectedStyle = Black; // Anvil Dark's own
			Assert.Equal("", settings.BasemapStyleId);
		}

		[Fact]
		public void Picking_a_theme_brings_its_own_basemap_and_ends_the_override()
		{
			var settings = new AppSettings { ThemeId = Dark.Id, BasemapStyleId = "grey" };
			var vm = New(settings);
			Assert.Same(Grey, vm.SelectedStyle);

			vm.SelectedTheme = Day;
			Assert.Same(Light, vm.SelectedStyle);
			Assert.Equal("", settings.BasemapStyleId);
			Assert.Same(Light, New(settings).SelectedStyle);
		}

		[Fact]
		public void An_unknown_saved_basemap_falls_back_to_the_theme()
		{
			var settings = new AppSettings { ThemeId = Dark.Id, BasemapStyleId = "removed-style" };
			Assert.Same(Black, New(settings).SelectedStyle);
		}

		[Theory]
		[InlineData(SettingsTab.Units)]
		[InlineData(SettingsTab.WindowMode)]
		public void The_retired_Units_and_Window_Mode_pages_open_General(SettingsTab retired)
		{
			var vm = New(new AppSettings { SettingsTabIndex = (int)retired });
			Assert.Equal((int)SettingsTab.Startup, vm.SettingsTabIndex);

			vm.OpenSettings(retired);
			Assert.Equal((int)SettingsTab.Startup, vm.SettingsTabIndex);
		}

		[Fact]
		public void The_Units_default_column_says_what_Restore_defaults_sets()
		{
			var vm = New(new AppSettings { ClockFormat = "24h", TimeZoneChoice = "utc", SpeedUnits = "kt", HeightUnits = "km", HailUnits = "cm" });
			vm.ResetUnits();

			Assert.Equal(vm.DefaultClockText, vm.ClockFormatLabels[vm.ClockFormatIndex]);
			Assert.Equal(vm.DefaultTimeZoneText, vm.TimeZoneLabels[vm.TimeZoneIndex]);
			Assert.Equal(vm.DefaultDistanceText, vm.DistanceUnitLabels[vm.DistanceUnitIndex]);
			Assert.Equal(vm.DefaultSpeedText, vm.SpeedUnitLabels[vm.SpeedUnitIndex]);
			Assert.Equal(vm.DefaultHeightText, vm.HeightUnitLabels[vm.HeightUnitIndex]);
			Assert.Equal(vm.DefaultHailText, vm.HailUnitLabels[vm.HailUnitIndex]);
		}

		[Fact]
		public void The_Startup_default_column_says_what_a_first_run_gets()
		{
			var fresh = new AppSettings();
			var vm = New(fresh);
			Assert.Equal(vm.DefaultStartupModeText, vm.StartupModeLabels[vm.StartupModeIndex]);
			Assert.Equal(fresh.StartupResume ? "Where I left off" : "A fixed start", vm.DefaultStartupResumeText);
		}

		[Fact]
		public void The_retired_Radar_Console_page_opens_Radar()
		{
			var vm = New(new AppSettings { SettingsTabIndex = (int)SettingsTab.RadarConsole });
			Assert.Equal((int)SettingsTab.Radar, vm.SettingsTabIndex);

			vm.OpenSettings(SettingsTab.RadarConsole);
			Assert.Equal((int)SettingsTab.Radar, vm.SettingsTabIndex);
		}

		[Fact]
		public void The_retired_Basemap_page_opens_Storage()
		{
			var vm = New(new AppSettings { SettingsTabIndex = (int)SettingsTab.Basemap });
			Assert.Equal((int)SettingsTab.Storage, vm.SettingsTabIndex);

			vm.OpenSettings(SettingsTab.Basemap);
			Assert.Equal((int)SettingsTab.Storage, vm.SettingsTabIndex);
		}

		[Fact]
		public void Radar_page_defaults_match_a_first_run()
		{
			var fresh = new AppSettings();
			var s = new AppSettings { RegimeAwarePolling = false, LivePollSeconds = 60, HideOtherSitesWhileLoaded = false,
				RevealSitesNearCursor = true, ScrubberStyle = "Hatch" };
			var vm = New(s);
			vm.ResetLivePolling();
			vm.ResetSiteKeys();
			vm.ResetScrubber();

			Assert.Equal((fresh.RegimeAwarePolling, fresh.LivePollSeconds, fresh.HideOtherSitesWhileLoaded, fresh.RevealSitesNearCursor, fresh.ScrubberStyle),
				(s.RegimeAwarePolling, s.LivePollSeconds, s.HideOtherSitesWhileLoaded, s.RevealSitesNearCursor, s.ScrubberStyle));
			Assert.Equal(vm.DefaultLivePollIntervalText, vm.Radar.LivePollIntervalLabels[vm.Radar.RefreshIntervalIndex]);
			Assert.Equal(vm.DefaultScrubberStyleText, vm.Radar.ScrubberStyleLabels[vm.Radar.ScrubberStyleIndex]);
		}

		// ── Settings → Rings & Ruler (2026-11-16): every card's Restore defaults = a first run, and says so in its DEFAULT column ──

		[Fact]
		public void A_ring_card_restores_its_own_defaults_and_the_theme_colour()
		{
			var s = new AppSettings { VelocityRingStyle = new RingStyle(90, 4.0, RingLines.Solid), VelocityRingColor = "#FF0000",
				ScopeColor = "#00FF00", OutlineRingStyle = new RingStyle(20, 3.0, RingLines.Dotted) };
			var rings = New(s).Radar.RangeRings;

			rings.Velocity.RestoreDefaults();
			Assert.Equal(RingStyle.VelocityDefault, s.VelocityRingStyle);
			Assert.Equal(ScopeColors.ThemeDefault, s.VelocityRingColor);
			Assert.Equal(new RingStyle(20, 3.0, RingLines.Dotted), s.OutlineRingStyle); // the other cards untouched
			Assert.Equal("#00FF00", s.ScopeColor);
			Assert.Equal("1.2 px", rings.Velocity.DefaultWidthText);
			Assert.Equal("Dashed", rings.Velocity.DefaultLineText);
		}

		[Fact]
		public void Rings_labels_and_ruler_restore_a_first_run()
		{
			var fresh = new AppSettings();
			var s = new AppSettings { ShowSiteRing = false, ShowDistanceRings = true, DistanceRingSpacing = 50, RulerAnchor = RulerAnchors.Location,
				DistanceLabelStyle = new RingLabelStyle(40, 20, 3), DistanceLabelBearing = 90, ShowDistanceLabelHandle = false, RingKnobSize = 44 };
			var vm = New(s);
			vm.ResetRingsPage();

			Assert.Equal((fresh.ShowSiteRing, fresh.ShowDistanceRings, fresh.DistanceRingSpacing, fresh.RulerAnchor),
				(s.ShowSiteRing, s.ShowDistanceRings, s.DistanceRingSpacing, s.RulerAnchor));
			Assert.Equal((RingLabelStyle.Default, fresh.DistanceLabelBearing, fresh.ShowDistanceLabelHandle, RingKnobSize.Default),
				(s.DistanceLabelStyle, s.DistanceLabelBearing, s.ShowDistanceLabelHandle, s.RingKnobSize));
			Assert.Equal(vm.DefaultRulerAnchorText, vm.Radar.RulerAnchorLabels[vm.Radar.RulerAnchorIndex]);
		}

		[Fact]
		public void The_retired_Edges_page_opens_Appearance()
		{
			var vm = New(new AppSettings { SettingsTabIndex = (int)SettingsTab.Edges });
			Assert.Equal((int)SettingsTab.Map, vm.SettingsTabIndex);

			vm.OpenSettings(SettingsTab.Edges);
			Assert.Equal((int)SettingsTab.Map, vm.SettingsTabIndex);
		}
	}
}
