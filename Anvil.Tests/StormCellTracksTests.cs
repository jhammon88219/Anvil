using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The pure half of the storm-cell overlay (<see cref="StormCellTracks"/>): IEM CSV parsing, which scan a
	/// radar frame shows, and each cell's past + forecast track. The Moore rows are REAL (IEM
	/// nexrad_storm_attrs, KTLX 2013-05-20 19:50–20:10Z, fetched 2026-09-26).
	/// </summary>
	public class StormCellTracksTests
	{
		// ⚠️ Header verbatim, typo and all (MAZ_DBZ_H) — the parser must find columns by name.
		private const string Moore = """
			VALID,STORM_ID,NEXRAD,AZIMUTH,RANGE,TVS,MESO,POSH,POH,MAX_SIZE,VIL,MAX_DBZ,MAZ_DBZ_H,TOP,DRCT,SKNT,LAT,LON
			201305201951,M0,TLX,270,17,NONE,7,70,80,1.5,30,63,22.4,22.4,246,21,35.32,-97.4538098657
			201305201955,S1,TLX,267,22,TVS,10,50,60,1.0,21,64,22.6,22.9,0,0,35.3095344099,-97.5147439476
			201305201955,M0,TLX,282,17,NONE,3,0,0,0.0,26,63,2.5,10.8,242,23,35.3511819832,-97.4497931791
			201305201959,V1,TLX,269,31,TVS,11,0,30,0.5,21,57,18.1,18.1,0,0,35.3150559079,-97.6171435333
			201305201959,M0,TLX,289,17,NONE,4,0,0,0.0,29,62,5.8,12.2,236,23,35.3688277531,-97.4437956424
			201305202003,V1,TLX,271,28,TVS,11,60,100,1.5,44,61,28.8,28.8,251,29,35.3243624342,-97.5763031176
			201305202003,M0,TLX,297,15,NONE,3,0,0,0.0,22,61,4.4,7.5,234,23,35.3805228074,-97.4155784796
			201305202008,V1,TLX,274,26,TVS,12,60,90,1.25,39,62,26.6,26.6,248,21,35.3362740208,-97.5552299553
			201305202008,M0,TLX,309,13,NONE,5,0,0,0.0,18,60,4.3,6.6,230,23,35.3934094815,-97.381103296
			""";

		private static DateTimeOffset Z(int h, int m) => new(2013, 5, 20, h, m, 0, TimeSpan.Zero);

		[Fact]
		public void ParseCsv_GroupsScansOldestFirst_AndReadsEveryField()
		{
			var scans = StormCellTracks.ParseCsv(Moore);

			Assert.Equal(new[] { Z(19, 51), Z(19, 55), Z(19, 59), Z(20, 3), Z(20, 8) }, scans.Select(s => s.Valid));
			var v1 = scans[3].Cells.Single(c => c.Id == "V1");
			Assert.Equal("TVS", v1.Tvs);
			Assert.Equal(11, v1.MesoRank);
			Assert.Equal(60, v1.Posh);
			Assert.Equal(1.5, v1.MaxHailIn);
			Assert.Equal(28.8, v1.MaxDbzHeightKft); // found under IEM's MAZ_DBZ_H
			Assert.Equal(251, v1.DirFromDeg);
			Assert.Equal(29, v1.SpeedKt);
			Assert.Equal("", scans[0].Cells.Single().Tvs); // "NONE" is no TVS
		}

		[Fact]
		public void ParseCsv_SkipsJunk_AndDedupesARepeatedCell()
		{
			// ⚠️ The raw-string fixture has no trailing newline — an appended row must bring its own.
			var csv = Moore + "\n201305201951,M0,TLX,1,1,NONE,1,0,0,0,0,0,0,0,0,0,35.0,-97.0\nnot,a,row\n";
			var scans = StormCellTracks.ParseCsv(csv);
			Assert.Single(scans[0].Cells);
			Assert.Equal(35.32, scans[0].Cells[0].Lat); // the FIRST row for (scan, id) wins

			Assert.Empty(StormCellTracks.ParseCsv("<!DOCTYPE html><html>404</html>"));
		}

		[Fact]
		public void SelectScan_NewestAtOrBeforeTheFrame_WithinTheLag()
		{
			var scans = StormCellTracks.ParseCsv(Moore);

			Assert.Equal(-1, StormCellTracks.SelectScan(scans, Z(19, 49)));            // before the first scan (past the 1-min slack)
			Assert.Equal(2, StormCellTracks.SelectScan(scans, Z(20, 1)));              // 19:59 is the newest at/before
			Assert.Equal(3, StormCellTracks.SelectScan(scans, Z(20, 3).AddSeconds(-30))); // a scan stamped just after its frame
			Assert.Equal(4, StormCellTracks.SelectScan(scans, Z(20, 20)));             // live: frame ahead of IEM, within 12 min
			Assert.Equal(-1, StormCellTracks.SelectScan(scans, Z(20, 21)));            // 13 min stale → nothing
		}

		[Fact]
		public void PastTrack_FollowsTheSameStorm_OldestFirst()
		{
			var scans = StormCellTracks.ParseCsv(Moore);

			var v1 = StormCellTracks.PastTrack(scans, 4, "V1"); // 20:08 → back to 19:59
			Assert.Equal(new[] { 35.3150559079, 35.3243624342 }, v1.Select(p => p.Lat));

			var m0 = StormCellTracks.PastTrack(scans, 4, "M0");
			Assert.Equal(4, m0.Count);
			Assert.Equal(35.32, m0[0].Lat); // oldest first
		}

		[Fact]
		public void PastTrack_BreaksWhereARecycledIdJumpsToAnotherStorm()
		{
			// M0 reappears 60 km away one scan later: a different storm wearing a recycled id.
			var csv = Moore + "\n201305202012,M0,TLX,1,1,NONE,0,0,0,0,0,0,0,0,230,23,35.9,-97.4\n";
			var scans = StormCellTracks.ParseCsv(csv);
			Assert.Empty(StormCellTracks.PastTrack(scans, 5, "M0"));
		}

		[Fact]
		public void PastTrack_BreaksAtALongGap()
		{
			// No M0 between 19:51 and 20:08 → 17 min apart, not one track.
			var csv = string.Join('\n', Moore.Split('\n').Where(l => !l.Contains(",M0,") || l.Contains("1951") || l.Contains("2008")));
			var scans = StormCellTracks.ParseCsv(csv);
			Assert.Empty(StormCellTracks.PastTrack(scans, scans.Count - 1, "M0"));
		}

		[Fact]
		public void Forecast_MovesTowardTheOppositeOfTheFromDirection()
		{
			// ⚠️ DRCT is where the storm comes FROM: the real V1 went from -97.617 to -97.555 (east) under
			// DRCT 251/248. So "from 270 at 30 kt" must head due EAST.
			var cell = new StormCell("X1", 35, -97, "", 0, 0, 0, 0, 0, 0, 0, 0, 270, 30);
			var f = StormCellTracks.Forecast(cell);

			Assert.Equal(4, f.Count);
			Assert.All(f, p => Assert.InRange(p.Lat, 34.99, 35.01));
			Assert.True(f[3].Lon > f[0].Lon && f[0].Lon > -97);
			Assert.InRange(StormCellTracks.DistanceKm(35, -97, f[3].Lat, f[3].Lon), 55.3, 55.8); // 30 kt × 1 h
		}

		[Fact]
		public void Forecast_NewCellWithNoMotion_HasNone()
		{
			var scans = StormCellTracks.ParseCsv(Moore);
			var s1 = scans[1].Cells.Single(c => c.Id == "S1"); // DRCT 0, SKNT 0
			Assert.Empty(StormCellTracks.Forecast(s1));
		}

		[Fact]
		public void PageJson_CarriesEveryScanWithTracks()
		{
			var json = StormCellTracks.ToPageJson("TLX", StormCellTracks.ParseCsv(Moore));
			var doc = System.Text.Json.JsonDocument.Parse(json).RootElement;

			Assert.Equal("TLX", doc.GetProperty("site").GetString());
			var last = doc.GetProperty("scans")[4];
			Assert.Equal(Z(20, 8).ToUnixTimeMilliseconds(), last.GetProperty("t").GetInt64());
			var v1 = last.GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "V1");
			Assert.Equal(2, v1.GetProperty("past").GetArrayLength());
			Assert.Equal(4, v1.GetProperty("fcst").GetArrayLength());
			Assert.Equal(-97.6171, v1.GetProperty("past")[0][0].GetDouble()); // [lon, lat]
		}
	}
}
