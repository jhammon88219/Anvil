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
