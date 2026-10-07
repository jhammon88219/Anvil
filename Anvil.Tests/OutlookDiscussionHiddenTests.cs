using System;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The Outlook Discussion window's risk cards + "are you in the risk area" line describe the PICKED outlook, so
	/// unticking the outlook's header box (map layer hidden) must not empty them (app note, 2026-10-07).
	/// </summary>
	public class OutlookDiscussionHiddenTests
	{
		private const string Text = "SPC AC 071630\n\nDay 1 Convective Outlook\n";

		private static OutlookViewModel ForeCast()
		{
			var product = new SpcOutlookProduct("d1c", 1, SpcOutlookType.Categorical, "Categorical", "d1c.geojson", "https://x/d1c.geojson");
			var spc = TemporalWindowPersistenceTests.Null<ISpcOutlookService>.Create(new()
			{
				["get_AvailableDays"] = _ => new[] { 1 },
				["GetProductsForDay"] = _ => new[] { product },
				["GetNarrativeAsync"] = _ => Task.FromResult<string?>(Text),
				["NarrativeRiskAreas"] = _ => new[] { new OutlookRiskArea("SLGT", 1000, 2000, Array.Empty<string>()) },
				["CategoricalFile"] = _ => "day1-categorical.geojson",
			});
			return new OutlookViewModel(TemporalWindowPersistenceTests.Null<IMapService>.Create(), spc,
				TemporalWindowPersistenceTests.Null<IDispatcher>.Create(), NullLogger<OutlookViewModel>.Instance);
		}

		[Fact]
		public async Task ForeCast_HiddenLayer_StillFeedsTheRiskCardsAndAreasFile()
		{
			var vm = ForeCast();
			vm.IsOutlookVisible = true;
			vm.IsShown = false;
			await vm.OnMapsReadyAsync();
			await Task.Delay(50); // the narrative fetch is fire-and-forget

			Assert.Equal("SLGT", Assert.Single(vm.NarrativeRiskAreas).Code);
			Assert.Equal("day1-categorical.geojson", vm.NarrativeAreasFile);
		}

		[Fact]
		public async Task PastCast_HiddenLayer_StillFeedsTheRiskCardsAndAreasFile()
		{
			var settings = new AppSettings();
			var svc = TemporalWindowPersistenceTests.Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var radar = new RadarViewModel(TemporalWindowPersistenceTests.Null<IMapService>.Create(),
				TemporalWindowPersistenceTests.Null<IRadarSiteProvider>.Create(), TemporalWindowPersistenceTests.Null<ILevel2RadarService>.Create(),
				TemporalWindowPersistenceTests.Null<IDowEventProvider>.Create(), svc, null);
			radar.IsPastEventMode = true;
			radar.MarkReplayWindowLoaded();
			var spc = TemporalWindowPersistenceTests.Null<ISpcOutlookService>.Create(new()
			{
				["EnsurePastOutlookAsync"] = a => Task.FromResult(new PastOutlookResult(true, (int)a![2]!, new[] { SpcOutlookType.Categorical }, null, null)),
				["GetPastNarrativeAsync"] = _ => Task.FromResult<string?>(Text),
				["PastNarrativeRiskAreas"] = _ => new[] { new OutlookRiskArea("ENH", 1000, 2000, Array.Empty<string>()) },
				["PastCategoricalFile"] = _ => "past-cat.geojson",
			});
			var past = new PastOutlookViewModel(TemporalWindowPersistenceTests.Null<IMapService>.Create(), spc, radar);
			past.IsShown = false;
			past.SelectedProductOption = past.ProductOptions.First(o => o.Type == SpcOutlookType.Categorical);
			await past.OnMapsReadyAsync();
			await Task.Delay(50);

			Assert.Equal("ENH", Assert.Single(past.NarrativeRiskAreas).Code);
			Assert.Equal("past-cat.geojson", past.NarrativeAreasFile);
		}

		private static T N<T>() where T : class => TemporalWindowPersistenceTests.Null<T>.Create();

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task Window_ForeCast_CardsFillWhateverTheBox(bool shown)
		{
			var text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "spc-disc-d1_20110427_1630.txt"));
			var product = new SpcOutlookProduct("d1c", 1, SpcOutlookType.Categorical, "Categorical", "d1c.geojson", "https://x/d1c.geojson");
			var spc = TemporalWindowPersistenceTests.Null<ISpcOutlookService>.Create(new()
			{
				["get_AvailableDays"] = _ => new[] { 1 },
				["GetProductsForDay"] = _ => new[] { product },
				["GetNarrativeAsync"] = _ => Task.FromResult<string?>(text),
				["NarrativeRiskAreas"] = _ => new[] { new OutlookRiskArea("HIGH", 1000, 2000, Array.Empty<string>()) },
			});
			var settings = new AppSettings { ForeCastOutlookShown = shown };
			var usageDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AnvilDiscTests", Guid.NewGuid().ToString("N"));
			var vm = new MapViewModel(N<IMapService>(), N<IStyleProvider>(), N<IThemeProvider>(), N<IRegionProvider>(),
				spc, N<ISpcWatchService>(), N<IWarningService>(), N<IStormReportService>(),
				N<IDamageSurveyService>(), N<IStormCellService>(), N<IPastAlertService>(), N<IMesoDiscussionService>(), N<ITropicalService>(), N<IRadarSiteProvider>(), N<ILevel2RadarService>(), N<ILocationService>(),
				N<IPlaceSearchService>(), N<IDowEventProvider>(), N<ISavedEventLibrary>(), N<IDispatcher>(),
				TemporalWindowPersistenceTests.Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings }),
				NullLoggerFactory.Instance, new SiteUsageStore(NullLogger<SiteUsageStore>.Instance, usageDir),
				N<IRadarNwsStatusService>(), N<IRadarUptimeService>(), N<IRadarMessageHistoryService>(),
				N<IRadarScanPatternService>(), new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, usageDir), null);
			await vm.Outlook.OnMapsReadyAsync();
			vm.IsForeCast = true;
			await Task.Delay(100);

			Assert.Equal(shown, vm.Outlook.IsShown);
			Assert.True(vm.OutlookDiscussion.IsDiscussion);
			Assert.True(vm.OutlookDiscussion.HasRiskCards);
			Assert.True(vm.OutlookDiscussion.HighCard.HasFigures);
		}
	}
}
