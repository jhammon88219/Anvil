using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// ForeCast's Short Range section: the byte encoding the page draws from (<see cref="ModelFieldEncoding"/>), the frame
	/// and mesh files (<see cref="ModelFieldFrames"/>), the activity row + summary, and MapViewModel's NowCast ⇄ MODEL rule.
	/// </summary>
	public class ShortRangeTests
	{
		// ── Encoding (mirrored in model-field.js: value = (code − offset) / scale) ──

		[Theory]
		[InlineData(9.9f, 0)]     // under radar's MIN_DBZ: not drawn
		[InlineData(10f, 86)]     // 10 × 2 + 66
		[InlineData(-32f, 0)]
		[InlineData(62.625f, 191)]
		[InlineData(500f, 255)]   // clamped
		public void Reflectivity_codes_are_level_iii_steps(float dbz, int code)
		{
			Assert.Equal(code, ModelFieldEncoding.Reflectivity.Code(dbz));
		}

		[Fact]
		public void Missing_is_nothing_and_codes_round_trip()
		{
			Assert.Equal(0, ModelFieldEncoding.Reflectivity.Code(float.NaN));
			Assert.Equal(47.5, ModelFieldEncoding.Reflectivity.ValueOf(ModelFieldEncoding.Reflectivity.Code(47.5f)));
			Assert.Equal(0, ModelFieldEncoding.UpdraftHelicity.Code(24f));
			Assert.Equal(76.0, ModelFieldEncoding.UpdraftHelicity.ValueOf(ModelFieldEncoding.UpdraftHelicity.Code(75.6f)));
		}

		// ── Frames + mesh ──

		[Fact]
		public void A_frame_is_gzipped_codes_in_grid_order()
		{
			var bytes = ModelFieldFrames.EncodeFrame(new[] { 5f, 20f, float.NaN, 70f }, ModelFieldEncoding.Reflectivity);
			Assert.Equal(new byte[] { 0x1f, 0x8b }, bytes[..2]);
			using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
			var codes = new MemoryStream();
			gz.CopyTo(codes);
			Assert.Equal(new byte[] { 0, 106, 0, 206 }, codes.ToArray());
		}

		[Fact]
		public void Run_so_far_is_the_cell_max_and_missing_loses()
		{
			var max = ModelFieldFrames.MaxOf(new[] { new[] { 10f, float.NaN, 5f }, new[] { 3f, 7f, float.NaN } });
			Assert.Equal(new[] { 10f, 7f, 5f }, max);
		}

		[Fact]
		public void The_mesh_spans_the_grid_corner_to_corner()
		{
			var grid = Grib2Decoder.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hrrr-refc-2026102115-f01.grib2"))).Grid;
			using var doc = JsonDocument.Parse(ModelFieldFrames.MeshJson(grid));
			var lonlat = doc.RootElement.GetProperty("lonlat").EnumerateArray().Select(e => e.GetDouble()).ToArray();
			Assert.Equal((ModelFieldFrames.MeshColumns + 1) * (ModelFieldFrames.MeshRows + 1) * 2, lonlat.Length);
			Assert.Equal(-122.720, lonlat[0], 3);   // SW, the first point
			Assert.Equal(21.138, lonlat[1], 3);
			Assert.Equal(-60.917, lonlat[^2], 3);   // NE, the last
			Assert.Equal(47.842, lonlat[^1], 3);
		}

		[Fact]
		public void Only_a_non_radar_ramp_rides_in_the_descriptor()
		{
			var grid = new LambertConformalGrid(3, 2, 35, 260, 262.5, 38.5, 38.5, 3000, 3000, 6_371_229);
			using var refl = JsonDocument.Parse(ModelFieldFrames.DescriptorJson("u", "m", grid, HrrrFields.CompositeReflectivity));
			Assert.False(refl.RootElement.TryGetProperty("stops", out _)); // the page uses radar's own table
			using var uh = JsonDocument.Parse(ModelFieldFrames.DescriptorJson("u", "m", grid, HrrrFields.UpdraftHelicity25));
			Assert.Equal(ModelRamps.UpdraftHelicityRamp.Stops.Count, uh.RootElement.GetProperty("stops").GetArrayLength());
			Assert.Equal("uh", uh.RootElement.GetProperty("ramp").GetString());
		}

		// ── Card + activity words ──

		[Theory]
		[InlineData(300, "f05")]
		[InlineData(315, "f05:15")]
		[InlineData(2880, "f48")]
		public void Lead_text(int lead, string text) => Assert.Equal(text, ShortRangeViewModel.LeadText(lead));

		[Fact]
		public void A_model_row_leads_the_summary_in_its_own_words()
		{
			var outlooks = new FeedStatus(LayerOrder.Outlook, "Outlooks", null);
			var hrrr = new FeedStatus(LayerOrder.ModelField, "HRRR", null);
			var activity = new TemporalActivityViewModel(null, new[] { outlooks }, () => "", _ => { });
			Assert.Equal("Outlooks starting", activity.Summary);

			activity.SetFeedShown(hrrr, true);
			Assert.Equal(2, activity.Feeds.Count);
			hrrr.SucceededWith("17Z · 7/18");
			Assert.Equal("HRRR 17Z · 7/18 · Outlooks starting", activity.Summary);

			activity.SetFeedShown(hrrr, false);
			Assert.Single(activity.Feeds);
			Assert.Equal("Outlooks starting", activity.Summary);
		}

		// ── NowCast ⇄ MODEL (MapViewModel) ──

		private static T N<T>() where T : class => TemporalWindowPersistenceTests.Null<T>.Create();

		private static MapViewModel NewMap()
		{
			var settings = new AppSettings();
			var settingsService = TemporalWindowPersistenceTests.Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var dir = Path.Combine(Path.GetTempPath(), "AnvilShortRangeTests", Guid.NewGuid().ToString("N"));
			return new MapViewModel(N<IMapService>(), N<IStyleProvider>(), N<IThemeProvider>(), N<IRegionProvider>(),
				N<ISpcOutlookService>(), N<ISpcWatchService>(), N<IWarningService>(), N<IStormReportService>(),
				N<IDamageSurveyService>(), N<IStormCellService>(), N<IPastAlertService>(), N<IMesoDiscussionService>(), N<ITropicalService>(),
				N<IRadarSiteProvider>(), N<ILevel2RadarService>(), N<ILocationService>(),
				N<IPlaceSearchService>(), N<IDowEventProvider>(), N<ISavedEventLibrary>(), N<IDispatcher>(),
				settingsService, NullLoggerFactory.Instance, new SiteUsageStore(NullLogger<SiteUsageStore>.Instance, dir),
				N<IRadarNwsStatusService>(), N<IRadarUptimeService>(), N<IRadarMessageHistoryService>(),
				N<IRadarScanPatternService>(), new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, dir), null);
		}

		[Fact]
		public void Ticking_the_model_turns_NowCast_off_and_unticking_brings_it_back()
		{
			var vm = NewMap();
			vm.IsNowCast = true;
			vm.IsForeCast = true;

			vm.ShortRange.ToggleShown();
			Assert.True(vm.ShortRange.IsDrawing);
			Assert.False(vm.IsNowCast);

			vm.ShortRange.ToggleShown();
			Assert.True(vm.IsNowCast);
		}

		[Fact]
		public void Turning_ForeCast_off_ends_the_model_and_brings_NowCast_back()
		{
			var vm = NewMap();
			vm.IsNowCast = true;
			vm.IsForeCast = true;
			vm.ShortRange.ToggleShown();

			vm.IsForeCast = false;
			Assert.False(vm.ShortRange.IsDrawing);
			Assert.True(vm.ShortRange.IsShown); // the tick is the user's; only the mode stopped
			Assert.True(vm.IsNowCast);
		}

		[Fact]
		public void NowCast_turned_on_unticks_a_drawing_model()
		{
			var vm = NewMap();
			vm.IsForeCast = true;
			vm.ShortRange.ToggleShown();

			vm.IsNowCast = true;
			Assert.False(vm.ShortRange.IsShown);
			Assert.True(vm.IsNowCast);
		}

		[Fact]
		public void Untick_without_NowCast_before_leaves_it_off()
		{
			var vm = NewMap();
			vm.IsForeCast = true;
			vm.ShortRange.ToggleShown();
			vm.ShortRange.ToggleShown();
			Assert.False(vm.IsNowCast);
		}

		[Fact]
		public void The_HRRR_activity_row_shows_only_while_ticked()
		{
			var vm = NewMap();
			Assert.DoesNotContain(vm.ShortRange.Status, vm.ForeCastActivity.Feeds);
			vm.ShortRange.ToggleShown();
			Assert.Contains(vm.ShortRange.Status, vm.ForeCastActivity.Feeds);
			vm.ShortRange.ToggleShown();
			Assert.DoesNotContain(vm.ShortRange.Status, vm.ForeCastActivity.Feeds);
		}
	}
}
