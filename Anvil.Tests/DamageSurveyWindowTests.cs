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
			var built = DamageSurveyService.BuildWindow(new[] { outerEf0, coreEf4 }, new[] { track }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), Ms(40), Ms(90));

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

			var built = DamageSurveyService.BuildWindow(new[] { farAway }, new[] { track }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), Ms(40), Ms(90));

			// Unlinked, so it spans minute 5 → 35 (not the track's 0 → 60) and falls outside the window.
			Assert.DoesNotContain(built, f => f.Layer == DatLayer.Areas);
		}

		[Fact]
		public void Unlinked_polygon_survives_a_window_starting_mid_tornado()
		{
			// No DAT track at all (2011-04-27 is mostly like this); every contour stamped at the tornado's start.
			var ef3 = Area(0, -88.0, -87.9, "EF3");
			var built = DamageSurveyService.BuildWindow(new[] { ef3 }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), Ms(20), Ms(80));
			Assert.Single(built);
			Assert.Null(built[0].TrackEf);
		}

		[Fact]
		public void Track_outside_the_window_is_dropped()
		{
			var early = Track(-120, -90, -97.0, -96.0);
			var built = DamageSurveyService.BuildWindow(Array.Empty<DatFeature>(), new[] { early }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), Ms(0), Ms(60));
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

		// ── NCEI Storm Events ─────────────────────────────────────────────────────────────────────────
		// The real rows for the May 24 2011 El Reno–Piedmont EF5 (NCEI details file, c20260323), trimmed to
		// the columns the parser reads. Times are CST — 20:50Z → 22:35Z.

		private const string SeHeader =
			"BEGIN_YEARMONTH,BEGIN_DAY,BEGIN_TIME,END_YEARMONTH,END_DAY,END_TIME,EPISODE_ID,EVENT_ID,STATE,EVENT_TYPE,CZ_NAME,WFO,CZ_TIMEZONE," +
			"INJURIES_DIRECT,INJURIES_INDIRECT,DEATHS_DIRECT,DEATHS_INDIRECT,TOR_F_SCALE,TOR_LENGTH,TOR_WIDTH,BEGIN_LAT,BEGIN_LON,END_LAT,END_LON,EVENT_NARRATIVE\n";

		private const string ElRenoRows =
			"201105,24,1450,201105,24,1555,52830,315837,OKLAHOMA,Tornado,CANADIAN,OUN,CST-6,100,0,7,0,EF5,39.6,1760,35.444,-98.287,35.725,-97.697,\"Began near Binger, crossed \"\"I-40\"\",\nthen Piedmont.\"\n" +
			"201105,24,1555,201105,24,1559,52830,315839,OKLAHOMA,Tornado,KINGFISHER,OUN,CST-6,0,0,0,0,EF3,2.2,1200,35.725,-97.697,35.747,-97.674,\n" +
			"201105,24,1559,201105,24,1635,52830,315848,OKLAHOMA,Tornado,LOGAN,OUN,CST-6,81,0,2,0,EF3,21.3,1200,35.747,-97.674,35.921,-97.356,Ended near Guthrie.\n" +
			"201105,24,1600,201105,24,1600,52830,999999,OKLAHOMA,Hail,LOGAN,OUN,CST-6,0,0,0,0,,,,35.8,-97.5,35.8,-97.5,not a tornado\n";

		private static System.Collections.Generic.List<SeSegment> ElReno() =>
			StormEvents.ParseTornadoRows(new System.IO.StringReader(SeHeader + ElRenoRows));

		private static long Utc(int h, int m, int day = 24) => new DateTimeOffset(2011, 5, day, h, m, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

		[Fact]
		public void Storm_events_rows_parse_in_standard_time_with_quoted_newlines()
		{
			var rows = ElReno();
			Assert.Equal(3, rows.Count); // the hail row is not a tornado
			Assert.Equal(Utc(20, 50), rows[0].T0); // 14:50 CST
			Assert.Equal(Utc(21, 55), rows[0].T1);
			Assert.Contains("\"I-40\"", rows[0].Narrative);
			Assert.Contains("\nthen Piedmont.", rows[0].Narrative);
			Assert.Equal(107, rows[0].Injuries + rows[0].Fatalities);
		}

		[Fact]
		public void County_rows_chain_into_one_path()
		{
			var tracks = StormEvents.BuildTracks(ElReno());
			var t = Assert.Single(tracks);
			Assert.Equal("se", t.Source);
			Assert.Equal("EF5", t.Ef);
			Assert.Equal(Utc(20, 50), t.T0);
			Assert.Equal(Utc(22, 35), t.T1);
			Assert.Equal(4, JsonDocument.Parse(t.Geometry).RootElement.GetProperty("coordinates").GetArrayLength()); // county lines agree: no jogs
			Assert.Equal("Canadian → Kingfisher → Logan Cos., Oklahoma", t.Name);
			Assert.Equal(9, t.Fatalities);
		}

		[Fact]
		public void Legacy_polygon_stamped_in_local_time_takes_the_storm_events_span()
		{
			// The DAT's own EF5 polygon for this tornado: stamped 14:50Z (CST written as UTC), no DAT track.
			var legacy = DamageSurveyService.TryParseFeature(Raw(
				"{\"type\":\"Polygon\",\"coordinates\":[[[-98.27,35.46],[-97.70,35.46],[-97.70,35.73],[-98.27,35.73],[-98.27,35.46]]]}",
				$"{{\"efscale\":\"EF5\",\"stormdate\":{Utc(14, 50)},\"comments\":null}}"),
				DatLayer.Areas)!;
			var se = StormEvents.BuildTracks(ElReno());

			// 5–7 PM CDT = 22:00–00:00Z.
			var built = DamageSurveyService.BuildWindow(new[] { legacy }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), se, Utc(22, 0), Utc(0, 0, 25));

			var area = Assert.Single(built, f => f.Layer == DatLayer.Areas);
			Assert.Equal(Utc(20, 50), area.T0);
			Assert.Equal(Utc(22, 35), area.T1);
			Assert.Single(built, f => f.Layer == DatLayer.Tracks); // the Storm Events path itself
		}

		[Fact]
		public void Side_by_side_legacy_polygons_each_find_their_own_tornado()
		{
			// May 24 2011, real rows: Chickasha EF4 (Grady 16:06 CST) and Bridge Creek–Goldsby EF4 (Grady
			// 16:26 CST) ran ~15 km apart. Their DAT polygons are stamped 16:06Z and 16:26Z (CST as UTC).
			var se = StormEvents.BuildTracks(StormEvents.ParseTornadoRows(new System.IO.StringReader(SeHeader +
				"201105,24,1606,201105,24,1638,52830,315858,OKLAHOMA,Tornado,GRADY,OUN,CST-6,0,0,0,0,EF4,21,880,35.008,-97.961,35.189,-97.67,\n" +
				"201105,24,1626,201105,24,1635,52830,315869,OKLAHOMA,Tornado,GRADY,OUN,CST-6,0,0,0,0,EF3,5.5,880,34.88,-97.732,34.939,-97.67,\n" +
				"201105,24,1635,201105,24,1705,52830,315875,OKLAHOMA,Tornado,MCCLAIN,OUN,CST-6,0,0,0,0,EF4,17.6,880,34.939,-97.67,35.141,-97.491,\n")));
			Assert.Equal(2, se.Count);

			DatFeature Poly(int h, int m, double lon, double lat) => DamageSurveyService.TryParseFeature(Raw(
				$"{{\"type\":\"Polygon\",\"coordinates\":[[[{lon - 0.02},{lat - 0.02}],[{lon + 0.02},{lat - 0.02}],[{lon + 0.02},{lat + 0.02}],[{lon - 0.02},{lat + 0.02}],[{lon - 0.02},{lat - 0.02}]]]}}",
				$"{{\"efscale\":\"EF4\",\"stormdate\":{Utc(h, m)}}}"), DatLayer.Areas)!;
			var chickasha = Poly(16, 6, -97.90, 35.05);   // on the Chickasha path
			// On the Goldsby path, but INSIDE both tracks' boxes and (at 22:26Z) inside both tracks' times —
			// raw nearest-in-time picks Chickasha (5h40 vs 6h); only shift + distance-to-line gets it right.
			var goldsby = Poly(16, 26, -97.70, 35.00);

			var built = DamageSurveyService.BuildWindow(new[] { chickasha, goldsby }, Array.Empty<DatFeature>(), Array.Empty<DatFeature>(), se, Utc(22, 0), Utc(0, 0, 25));

			var areas = built.Where(f => f.Layer == DatLayer.Areas).ToList();
			Assert.Contains(areas, a => a.T0 == Utc(22, 6) && a.Name == "Grady Co., Oklahoma");
			Assert.Contains(areas, a => a.T0 == Utc(22, 26) && a.Name == "Grady → Mcclain Cos., Oklahoma");
		}

		[Fact]
		public void Matching_DAT_track_keeps_its_shape_takes_storm_events_times_and_replaces_the_duplicate()
		{
			var dat = DamageSurveyService.TryParseFeature(Raw(
				"{\"type\":\"LineString\",\"coordinates\":[[-98.28,35.44],[-98.0,35.6],[-97.36,35.92]]}",
				$"{{\"efscale\":\"EF5\",\"event_id\":\"El Reno-Piedmont\",\"wfo\":\"OUN\",\"stormdate\":{Utc(14, 50)},\"starttime\":{Utc(14, 50)},\"endtime\":{Utc(16, 35)}}}"),
				DatLayer.Tracks)!;

			var built = DamageSurveyService.BuildWindow(Array.Empty<DatFeature>(), new[] { dat }, Array.Empty<DatFeature>(),
				StormEvents.BuildTracks(ElReno()), Utc(22, 0), Utc(0, 0, 25));

			var track = Assert.Single(built);
			Assert.Equal("dat", track.Source);
			Assert.Equal("El Reno-Piedmont", track.Name);
			Assert.Equal(Utc(20, 50), track.T0);
		}

		[Theory]
		[InlineData("CST-6", -6)]
		[InlineData("CST", -6)]
		[InlineData("EST-5", -5)]
		[InlineData("MST", -7)]
		[InlineData("HST-10", -10)]
		[InlineData("GST10", 10)]
		public void Storm_events_zones_resolve(string tz, int hours) => Assert.Equal(hours, StormEvents.UtcOffsetHours(tz));

		[Fact]
		public void Sloppy_county_line_points_still_chain_when_the_time_hands_off()
		{
			// 1999-05-03 Bridge Creek–Moore F5, the real rows: McClain ends at -97.55, Cleveland begins at -97.60.
			var rows = StormEvents.ParseTornadoRows(new System.IO.StringReader(SeHeader +
				"199905,3,1726,199905,3,1800,2409595,5705284,OKLAHOMA,Tornado,GRADY,OUN,CST,0,0,0,0,F5,,,35.13,-97.85,35.23,-97.67,\n" +
				"199905,3,1800,199905,3,1812,2409595,5705285,OKLAHOMA,Tornado,MCCLAIN,OUN,CST,0,0,0,0,F4,,,35.25,-97.67,35.28,-97.55,\n" +
				"199905,3,1812,199905,3,1830,2409595,5705286,OKLAHOMA,Tornado,CLEVELAND,OUN,CST,0,0,0,0,F5,,,35.3,-97.6,35.37,-97.45,\n" +
				"199905,3,1830,199905,3,1848,2409595,5705287,OKLAHOMA,Tornado,OKLAHOMA,OUN,CST,0,0,0,0,F4,,,35.4,-97.45,35.45,-97.43,\n"));
			var t = Assert.Single(StormEvents.BuildTracks(rows));
			Assert.Equal("F5", t.Label);
			Assert.Equal(new DateTimeOffset(1999, 5, 4, 0, 48, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), t.T1);
		}

		[Fact]
		public void The_exact_handoff_wins_over_an_earlier_nearby_row()
		{
			// May 24 2011, real rows: the Chickasha EF4's McClain row ends at the Cleveland row's start, same
			// minute. An unrelated McClain EF0 ended 7 min earlier ~7 km away — and comes FIRST in the file.
			var rows = StormEvents.ParseTornadoRows(new System.IO.StringReader(SeHeader +
				"201105,24,1645,201105,24,1646,52830,315861,OKLAHOMA,Tornado,MCCLAIN,OUN,CST-6,0,0,0,0,EF0,0.7,50,35.242,-97.628,35.243,-97.619,\n" +
				"201105,24,1638,201105,24,1653,52830,315859,OKLAHOMA,Tornado,MCCLAIN,OUN,CST-6,0,0,0,0,EF4,9.6,650,35.189,-97.67,35.305,-97.589,\n" +
				"201105,24,1653,201105,24,1702,52830,315860,OKLAHOMA,Tornado,CLEVELAND,OUN,CST-6,0,0,0,0,EF0,2.7,200,35.305,-97.589,35.311,-97.571,\n"));
			var tracks = StormEvents.BuildTracks(rows);
			Assert.Equal(2, tracks.Count);
			Assert.Contains(tracks, t => t.Ef == "EF4" && t.Name == "Mcclain → Cleveland Cos., Oklahoma");
		}

		[Fact]
		public void A_new_tornado_in_the_same_county_is_not_welded_on()
		{
			// Cyclic storm: the second tornado starts 3 km and 2 minutes after the first ropes out — same county.
			var rows = StormEvents.ParseTornadoRows(new System.IO.StringReader(SeHeader +
				"201305,20,1400,201305,20,1410,7,1,OKLAHOMA,Tornado,GRADY,OUN,CST-6,0,0,0,0,EF1,,,35.00,-97.90,35.05,-97.80,\n" +
				"201305,20,1412,201305,20,1420,7,2,OKLAHOMA,Tornado,GRADY,OUN,CST-6,0,0,0,0,EF2,,,35.07,-97.79,35.10,-97.70,\n"));
			Assert.Equal(2, StormEvents.BuildTracks(rows).Count);
		}

		[Fact]
		public void Old_F_scale_is_coloured_as_EF_but_labelled_F()
		{
			var rows = StormEvents.ParseTornadoRows(new System.IO.StringReader(SeHeader +
				"199905,3,1826,199905,3,1935,1,2,OKLAHOMA,Tornado,GRADY,OUN,CST,0,0,36,0,F5,37,1430,35.0,-97.9,35.3,-97.5,\n"));
			var t = Assert.Single(StormEvents.BuildTracks(rows));
			Assert.Equal("EF5", t.Ef);
			Assert.Equal("F5", t.Label);
		}
	}
}
