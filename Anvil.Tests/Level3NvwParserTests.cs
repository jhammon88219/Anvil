using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Pins <see cref="Level3NvwParser"/> against a REAL NEXRAD Level III product 48 file.
	///
	/// <para>⚠️ This is the only test in the project backed by a checked-in binary. Everything else here is
	/// synthesized (see <c>SyntheticVolume</c>), deliberately. The exception is made because a synthetic NVW
	/// stream would be written from the same understanding of the format as the parser, so it could only ever
	/// confirm our own assumptions — the format is exactly the thing under test.</para>
	///
	/// <para>The strongest assertion here is <see cref="AltitudeColumnIsMsl_ProvenAgainstBeamGeometry"/>,
	/// which needs no external ground truth: beam height is fully determined by slant range and elevation
	/// angle, so ALT×100 minus the computed beam height must equal the radar's own height for every level.
	/// That single check catches byte-offset errors, unit errors and datum errors at once.</para>
	/// </summary>
	public class Level3NvwParserTests
	{
		// Values below were read out of the reference product by hand before the parser existed, so they are
		// an independent expectation rather than a recording of parser output.
		private const string Site = "TLX";
		private const double RadarHeightFtMsl = 1277.0;   // PDB halfword 15
		private const int ExpectedLevels = 27;

		private static byte[] LoadFixture()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TLX_NVW_2020_03_31_00_02_54.bin");
			Assert.True(File.Exists(path), $"fixture missing: {path}");
			return File.ReadAllBytes(path);
		}

		private static WindProfile ParseFixture()
		{
			var profile = Level3NvwParser.Parse(LoadFixture(), Site);
			Assert.NotNull(profile);
			return profile!;
		}

		[Fact]
		public void ParsesTheReferenceProduct()
		{
			var p = ParseFixture();
			Assert.Equal("NVW", p.Source);
			Assert.Equal(Site, p.SiteId);
			Assert.Equal(RadarHeightFtMsl, p.RadarHeightFtMsl, 0);
			Assert.Equal(ExpectedLevels, p.Levels.Count);
		}

		[Fact]
		public void LevelsAreAscendingByHeight()
		{
			var p = ParseFixture();
			for (var i = 1; i < p.Levels.Count; i++)
			{
				Assert.True(p.Levels[i].HeightAglM > p.Levels[i - 1].HeightAglM,
					$"level {i} at {p.Levels[i].HeightAglM} m is not above {p.Levels[i - 1].HeightAglM} m");
			}
		}

		[Fact]
		public void HeightsAreAboveRadarLevelNotMsl()
		{
			var p = ParseFixture();

			// Lowest reported level is ALT=016 -> 1600 ft MSL. Above radar level that is 1600 - 1277 = 323 ft.
			// If the radar-height subtraction were dropped, this would come back near 488 m instead of ~98 m.
			var lowest = p.Levels[0].HeightAglM;
			Assert.InRange(lowest, 90.0, 110.0);

			// Highest is ALT=180 -> 18000 ft MSL -> 16723 ft ARL -> ~5097 m.
			var highest = p.Levels[^1].HeightAglM;
			Assert.InRange(highest, 5050.0, 5150.0);
		}

		[Fact]
		public void UvComponentsAgreeWithReportedSpeedAndDirection()
		{
			// Internal consistency: the file carries U/V AND speed/direction, computed by the RPG from the
			// same fit. If our column indices were off by one, these would diverge immediately.
			var p = ParseFixture();
			foreach (var lvl in p.Levels)
			{
				var speedKt = Math.Sqrt((lvl.U * lvl.U) + (lvl.V * lvl.V)) / 0.514444;
				Assert.True(Math.Abs(speedKt - lvl.SpeedKt) < 1.5,
					$"speed mismatch at {lvl.HeightAglM:F0} m: u/v give {speedKt:F1} kt, file says {lvl.SpeedKt}");

				var dirFrom = (270.0 - (Math.Atan2(lvl.V, lvl.U) * 180.0 / Math.PI)) % 360.0;
				if (dirFrom < 0)
				{
					dirFrom += 360.0;
				}

				var delta = Math.Abs(((dirFrom - lvl.DirectionFromDeg + 180.0) % 360.0) - 180.0);
				Assert.True(delta < 2.0,
					$"direction mismatch at {lvl.HeightAglM:F0} m: u/v give {dirFrom:F0}, file says {lvl.DirectionFromDeg}");
			}
		}

		[Fact]
		public void RmsIsPresentAndPlausible()
		{
			// The whole reason to parse the tabular block rather than the symbology barbs: a real per-level
			// RMS in knots, not a 5-level colour tier.
			var p = ParseFixture();
			Assert.All(p.Levels, lvl => Assert.InRange(lvl.RmsKt, 0.0, 50.0));
			Assert.Contains(p.Levels, lvl => lvl.RmsKt > 0.0);
		}

		[Fact]
		public void ValidTimeMatchesTheProduct()
		{
			// 2020-03-31 00:02Z, from the product's own header. Guards the modified-Julian epoch, where the
			// day-1 = 1 Jan 1970 convention makes an off-by-one silent.
			var p = ParseFixture();
			Assert.Equal(2020, p.ValidTimeUtc.Year);
			Assert.Equal(3, p.ValidTimeUtc.Month);
			Assert.Equal(31, p.ValidTimeUtc.Day);
			Assert.Equal(DateTimeKind.Utc, p.ValidTimeUtc.Kind);
		}

		[Fact]
		public void AltitudeColumnIsMsl_ProvenAgainstBeamGeometry()
		{
			// Independent proof of the datum, needing no external truth: under the 4/3-earth model a level's
			// height above the radar follows from its slant range and elevation angle alone. So
			// (reported ALT in ft) - (computed beam height in ft) must equal the radar's height above sea
			// level -- the same constant at every level -- iff ALT is MSL.
			//
			// A MEDIAN is used, not a mean: the reference product contains one level whose SRNG/ELEV are
			// internally inconsistent (they imply 33,000 ft against a reported 15,200 ft), and a mean lets
			// that single row swamp the result. This is also why the parser never derives height from
			// SRNG/ELEV.
			var rows = ReadRawTableRows();
			Assert.Equal(ExpectedLevels, rows.Count);

			const double ka = 8494660.0;    // 4/3-earth effective radius, metres
			const double nm = 1852.0;
			const double ft = 0.3048;

			var diffs = new List<double>();
			foreach (var (altHundredsFt, srngNm, elevDeg) in rows)
			{
				var r = srngNm * nm;
				var th = elevDeg * Math.PI / 180.0;
				var beamM = Math.Sqrt((r * r) + (ka * ka) + (2 * r * ka * Math.Sin(th))) - ka;
				diffs.Add((altHundredsFt * 100.0) - (beamM / ft));
			}

			diffs.Sort();
			var median = diffs[diffs.Count / 2];
			Assert.True(Math.Abs(median - RadarHeightFtMsl) < 150.0,
				$"ALT does not look like MSL: median offset {median:F0} ft vs radar height {RadarHeightFtMsl} ft");

			var inliers = diffs.Count(d => Math.Abs(d - median) < 500.0);
			Assert.True(inliers >= 25, $"only {inliers}/{diffs.Count} levels agree on the datum");
		}

		/// <summary>Pulls (ALT, SRNG, ELEV) straight out of the fixture's ASCII table, independently of the
		/// parser, so the geometry proof above cannot be satisfied by a parser bug.</summary>
		private static List<(int AltHundredsFt, double SrngNm, double ElevDeg)> ReadRawTableRows()
		{
			// ⚠️ Tabular rows are LENGTH-PREFIXED records, not newline-separated text, so the inflated bytes
			// contain no line breaks to split on. Match the row's shape instead. W and DIV read "NA" when
			// absent, which is why those two groups are alternations.
			var text = System.Text.Encoding.ASCII.GetString(Inflate(LoadFixture()));
			var row = new System.Text.RegularExpressions.Regex(
				@"\s(\d{3})\s+(-?\d+\.\d)\s+(-?\d+\.\d)\s+(?:NA|-?\d+\.\d)\s+(\d{3})\s+(\d{3})"
				+ @"\s+(\d+\.\d)\s+(?:NA|-?\d+\.\d+)\s+(\d+\.\d+)\s+(\d+\.\d)\s");

			var rows = new List<(int, double, double)>();
			foreach (System.Text.RegularExpressions.Match m in row.Matches(text))
			{
				rows.Add((
					int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
					double.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture),
					double.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture)));
			}

			return rows;
		}

		/// <summary>Minimal local inflate for the raw-table reader — deliberately not the parser's.</summary>
		private static byte[] Inflate(byte[] file)
		{
			var output = new MemoryStream();
			for (var i = 0; i + 1 < file.Length; i++)
			{
				if ((file[i] & 0x0F) != 8 || (((file[i] << 8) | file[i + 1]) % 31) != 0)
				{
					continue;
				}

				// Same commit-only-on-success rule as the parser: a false-positive header can emit bytes
				// before it fails, and appending those corrupts the assembled message.
				var frame = new MemoryStream();
				try
				{
					using var input = new MemoryStream(file, i, file.Length - i, writable: false);
					using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
					z.CopyTo(frame);
				}
				catch (Exception)
				{
					continue;
				}

				frame.Position = 0;
				frame.CopyTo(output);
			}

			return output.ToArray();
		}

		[Fact]
		public void MalformedInputReturnsNullRatherThanThrowing()
		{
			// "No profile" is an ordinary outcome, not an error: clear air aloft legitimately produces a
			// product with an empty table, and the caller must fall through to the next source.
			Assert.Null(Level3NvwParser.Parse(null!, Site));
			Assert.Null(Level3NvwParser.Parse(Array.Empty<byte>(), Site));
			Assert.Null(Level3NvwParser.Parse(new byte[512], Site));

			var truncated = LoadFixture()[..200];
			Assert.Null(Level3NvwParser.Parse(truncated, Site));

			var corrupted = LoadFixture();
			for (var i = 100; i < corrupted.Length; i++)
			{
				corrupted[i] ^= 0xFF;
			}

			var ex = Record.Exception(() => Level3NvwParser.Parse(corrupted, Site));
			Assert.Null(ex);
		}
	}
}
