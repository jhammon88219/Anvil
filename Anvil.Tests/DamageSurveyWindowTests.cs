using System;
using System.Linq;
using System.Text.Json;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The damage-survey window core (<see cref="DamageSurveyService.BuildWindow"/> and the raw-feature
	/// parse). The DAT publishes no link between a damage polygon and its track (<c>path_guid</c> is null),
	/// and a polygon's own time is the time of THAT contour — so a window catching only the later half of a
	/// tornado must still keep the tornado's early outer contour, which only the time+place link can do.
	/// HTTP-free, deterministic.
	/// </summary>
	public class DamageSurveyWindowTests
	{
		private static readonly DateTimeOffset Base = new(2024, 5, 7, 2, 0, 0, TimeSpan.Zero);
		private static long Ms(int minutes) => Base.AddMinutes(minutes).ToUnixTimeMilliseconds();

		// A raw DAT GeoJSON feature, shaped as the FeatureServer returns it.
		private static JsonElement Raw(string geometryJson, string propsJson) =>
			JsonDocument.Parse($"{{\"type\":\"Feature\",\"geometry\":{geometryJson},\"properties\":{propsJson}}}").RootElement;

		private static DatFeature Track(int startMin, int endMin, double lon0, double lon1, string ef = "EF4", string name = "Elgin") =>
			DamageSurveyService.TryParseFeature(Raw(
				$"{{\"type\":\"LineString\",\"coordinates\":[[{lon0},35.0],[{lon1},35.1]]}}",
				$"{{\"efscale\":\"{ef}\",\"event_id\":\"{name}\",\"wfo\":\"TSA\",\"stormdate\":{Ms(startMin)},\"starttime\":{Ms(startMin)},\"endtime\":{Ms(endMin)},\"maxwind\":180,\"length\":40.8,\"width\":1700,\"injuries\":33,\"fatalities\":2}}"),
				DatLayer.Tracks)!;

		private static DatFeature Area(int atMin, double lon0, double lon1, string ef) =>
			DamageSurveyService.TryParseFeature(Raw(
				$"{{\"type\":\"Polygon\",\"coordinates\":[[[{lon0},34.99],[{lon1},34.99],[{lon1},35.11],[{lon0},35.11],[{lon0},34.99]]]}}",
				$"{{\"efscale\":\"{ef}\",\"event_id\":\"\",\"stormdate\":{Ms(atMin)},\"path_guid\":null,\"comments\":\"\"}}"),
				DatLayer.Areas)!;

		[Fact]
		public void Polygon_takes_its_tracks_span_so_an_early_contour_survives_a_late_window()
		{
			var track = Track(0, 60, -97.0, -96.0);
			var outerEf0 = Area(0, -97.0, -96.0, "EF0");   // stamped at the tornado's START
			var coreEf4 = Area(30, -96.6, -96.4, "EF4");

			// Window catches only the second half of the tornado.
			var built = DamageSurveyService.BuildWindow(new[] { outerEf0, coreEf4 }, new[] { track }, Array.Empty<DatFeature>(), Ms(40), Ms(90));

			var areas = built.Where(f => f.Layer == DatLayer.Areas).ToList();
			Assert.Equal(2, areas.Count);
			Assert.All(areas, a => Assert.Equal("Elgin", a.Name));
			Assert.All(areas, a => Assert.Equal("EF4", a.TrackEf));
			Assert.Equal(new[] { "EF0", "EF4" }, areas.Select(a => a.Ef)); // lowest first: higher draws on top
			Assert.Single(built, f => f.Layer == DatLayer.Tracks);
		}

		[Fact]
		public void Polygon_is_not_linked_to_a_track_elsewhere_at_the_same_time()
		{
			var track = Track(0, 60, -97.0, -96.0);
			var farAway = Area(5, -90.0, -89.9, "EF1");   // inside the track's time, wrong place

			var built = DamageSurveyService.BuildWindow(new[] { farAway }, new[] { track }, Array.Empty<DatFeature>(), Ms(40), Ms(90));

			// Unlinked, so it spans minute 5 → 35 (not the track's 0 → 60) and falls outside the window.
			Assert.DoesNotContain(built, f => f.Layer == DatLayer.Areas);
		}

		[Fact]
		public void Unlinked_polygon_survives_a_window_starting_mid_tornado()
		{
			// No DAT track at all (2011-04-27 is mostly like this); every contour stamped at the tornado's start.
			var ef3 = Area(0, -88.0, -87.9, "EF3");
			var built = DamageSurveyService.BuildWindow(new[] { ef3 }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), Ms(20), Ms(80));
			Assert.Single(built);
			Assert.Null(built[0].TrackEf);
		}

		[Fact]
		public void Track_outside_the_window_is_dropped()
		{
			var early = Track(-120, -90, -97.0, -96.0);
			var built = DamageSurveyService.BuildWindow(Array.Empty<DatFeature>(), new[] { early }, Array.Empty<DatFeature>(), Ms(0), Ms(60));
			Assert.Empty(built);
		}

		[Theory]
		[InlineData("TSTM/Wind")]
		[InlineData("Tropical")]
		[InlineData("Wind")]
		public void Non_tornado_entries_are_dropped(string ef) => Assert.Null(DamageSurveyService.NormalizeEf(ef));

		[Theory]
		[InlineData("ef0", "EF0")]
		[InlineData("EF3+", "EF3+")]
		[InlineData("UNKNOWN", "N/A")]
		[InlineData(null, "N/A")]
		public void Ratings_normalize_as_the_DAT_renderer_groups_them(string? raw, string expected) =>
			Assert.Equal(expected, DamageSurveyService.NormalizeEf(raw));

		[Fact]
		public void Unknown_numbers_are_absent_not_negative()
		{
			// DAT writes -99 for "unknown"; the point layer's windspeed is a string.
			var point = DamageSurveyService.TryParseFeature(Raw(
				"{\"type\":\"Point\",\"coordinates\":[-100.69,40.88]}",
				$"{{\"efscale\":\"EF0\",\"stormdate\":{Ms(0)},\"windspeed\":\"60\",\"office\":\"LBF\",\"injuries\":-99}}"),
				DatLayer.Points)!;
			Assert.Equal(60, point.Wind);
			Assert.Equal("LBF", point.Wfo);
			Assert.Equal(0, point.Injuries);
		}

		[Fact]
		public void Days_reach_back_for_tracks_that_started_before_midnight()
		{
			var start = new DateTimeOffset(2024, 5, 7, 1, 0, 0, TimeSpan.Zero);
			var days = DamageSurveyService.DaysFor(start, start.AddHours(2)).ToList();
			Assert.Equal(new[] { new DateOnly(2024, 5, 6), new DateOnly(2024, 5, 7) }, days);

			// A window ending exactly at midnight does not pull the next day.
			var evening = new DateTimeOffset(2024, 5, 6, 18, 0, 0, TimeSpan.Zero);
			Assert.Equal(new[] { new DateOnly(2024, 5, 6) }, DamageSurveyService.DaysFor(evening, evening.AddHours(6)).ToList());
		}
	}
}
