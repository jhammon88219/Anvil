using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="TrackedCellMotion"/>: the SRV storm motion's first choice, the vector mean of the tracked
	/// cells' motion over a span. Directions in the cell table are FROM; the result is the heading TOWARD.
	/// </summary>
	public class TrackedCellMotionTests
	{
		private static readonly DateTimeOffset T0 = new(2013, 5, 20, 20, 0, 0, TimeSpan.Zero);

		private static StormCell Cell(string id, int fromDeg, int kt) =>
			new(id, 35, -97, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, fromDeg, kt);

		private static StormCellScan Scan(int minute, params StormCell[] cells) => new(T0.AddMinutes(minute), cells);

		[Fact]
		public void Mean_is_a_vector_average_and_heads_toward_where_the_storms_go()
		{
			// Two cells from the west and two from the south, all 20 kt → toward the NE at 20/√2.
			var scans = new List<StormCellScan>
			{
				Scan(0, Cell("A0", 270, 20), Cell("B0", 180, 20)),
				Scan(5, Cell("A0", 270, 20), Cell("B0", 180, 20)),
			};

			var m = TrackedCellMotion.Mean(scans, T0, T0.AddMinutes(5));

			Assert.NotNull(m);
			Assert.Equal(45, m!.HeadingDeg, 3);
			Assert.Equal(20 / Math.Sqrt(2) * 0.514444, m.SpeedMs, 3);
			Assert.Equal(2, m.Cells);
			Assert.Equal(4, m.Sightings);
		}

		[Fact]
		public void Directions_either_side_of_north_average_to_south_not_north()
		{
			// From 10° and from 350° = moving toward 190° and 170° → toward 180°. A plain average of the
			// FROM angles (180) turned around would say toward 0° — the storms' exact opposite.
			var scans = new List<StormCellScan> { Scan(0, Cell("A0", 10, 30), Cell("B0", 350, 30), Cell("C0", 10, 30), Cell("D0", 350, 30)) };

			var m = TrackedCellMotion.Mean(scans, T0, T0);

			Assert.NotNull(m);
			Assert.Equal(180, m!.HeadingDeg, 3);
		}

		[Fact]
		public void Too_few_moving_sightings_is_no_answer_and_new_cells_do_not_count()
		{
			// Two moving sightings plus two first detections (speed 0, no motion yet) → below the minimum.
			var scans = new List<StormCellScan> { Scan(0, Cell("A0", 270, 20), Cell("B0", 270, 20), Cell("C0", 0, 0), Cell("D0", 0, 0)) };

			Assert.Equal(3, TrackedCellMotion.MinSightings);
			Assert.Null(TrackedCellMotion.Mean(scans, T0, T0));
		}

		[Fact]
		public void Only_scans_inside_the_span_count()
		{
			// The fetch reaches back before the loop for past tracks; those scans must not move the motion.
			var scans = new List<StormCellScan>
			{
				Scan(-20, Cell("X0", 90, 40), Cell("Y0", 90, 40), Cell("Z0", 90, 40)),   // before the span: toward W
				Scan(0, Cell("A0", 270, 20), Cell("B0", 270, 20), Cell("C0", 270, 20)),  // inside: toward E
				Scan(30, Cell("X0", 90, 40), Cell("Y0", 90, 40), Cell("Z0", 90, 40)),    // after
			};

			var m = TrackedCellMotion.Mean(scans, T0, T0.AddMinutes(10));

			Assert.NotNull(m);
			Assert.Equal(90, m!.HeadingDeg, 3);
			Assert.Equal(3, m.Sightings);
		}

		[Fact]
		public void Real_Moore_rows_give_the_tracked_northeast_motion()
		{
			// IEM nexrad_storm_attrs, KTLX 2013-05-20 19:51–20:08Z (the StormCellTracksTests rows).
			const string csv = """
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
			var scans = StormCellTracks.ParseCsv(csv);

			var m = TrackedCellMotion.Mean(scans, new DateTimeOffset(2013, 5, 20, 19, 50, 0, TimeSpan.Zero), T0.AddMinutes(10));

			Assert.NotNull(m);
			Assert.Equal(7, m!.Sightings);              // the two speed-0 first detections are skipped
			Assert.Equal(2, m.Cells);                    // M0 and V1 (S1 never has a motion)
			Assert.InRange(m.HeadingDeg, 55, 68);       // toward the ENE, as the storms moved
			Assert.InRange(m.SpeedMs / 0.514444, 21, 25);
		}
	}
}
