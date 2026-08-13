using System.Collections.Generic;
using System.Text;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Live-frame sweep selection — <c>Level2Format.SelectLatestSweep</c>, which picks the surveillance cut
	/// to render from a growing chunk volume and pairs it with its Doppler (velocity) companion. Guards the
	/// KDVN "stale frame" fix: a stray 1-block ORPHAN cut (elevation number 0 / unparseable angle — a
	/// transition radial) can wedge between a SAILS/MRLE base surveillance cut and its Doppler companion.
	/// The companion search must walk PAST such orphans; the pre-fix code looked only at the immediately
	/// adjacent cut (<c>cuts[si+1]</c>), so those scans never paired and the display froze on the volume's
	/// oldest base scan. Measured KDVN cut table: <c>#7@0.44[38..44]R #0@?[44..45] #8@0.44[45..51]RV</c>.
	/// </summary>
	public class LiveSweepPairingTests
	{
		private static readonly byte[] Icao = Encoding.ASCII.GetBytes(SyntheticVolume.DefaultIcao);
		private static readonly byte[] Header = new byte[24];

		// A decoded chunk block paired with its elevation NUMBER, as SelectLatestSweep consumes them.
		private static (byte[] block, int elev) Radial(int elev, float angle, string marker, bool refl, bool vel)
			=> (SyntheticVolume.Radial(elev, angle, marker, reflectivity: refl, velocity: vel), elev);

		private static (byte[] block, int elev) Meta(string marker)
			=> (SyntheticVolume.Metadata(marker), 0); // elev 0 -> its own cut, no ICAO -> NaN angle (an orphan)

		/// <summary>
		/// ⚠️ THE regression. Two base 0.44° scans: the first has its Doppler ADJACENT (always paired), the
		/// second (newer, a SAILS re-scan) has a NaN-angle ORPHAN wedged before its Doppler. The selector must
		/// still pick the NEWER scan (walking past the orphan to its companion) — pre-fix it fell back to the
		/// older scan, freezing the frame. Asserts the newer scan's markers made it into the extracted tilt
		/// and the older scan's did not.
		/// </summary>
		[Fact]
		public void PairsDopplerCompanionAcrossAnOrphanCut()
		{
			var blocks = new List<(byte[] block, int elev)>
			{
				Meta("lead"),                              // leading metadata
				Radial(1, 0.44f, "sv01", refl: true,  vel: false),  // base surveillance #1
				Radial(2, 0.44f, "dv01", refl: true,  vel: true),   // base Doppler #1 (adjacent)
				Radial(3, 0.88f, "hgh3", refl: true,  vel: false),  // 0.9° tilt between the two base scans
				Radial(7, 0.44f, "sv02", refl: true,  vel: false),  // SAILS base surveillance #2 (NEWER)
				Meta("orph"),                              // <-- ORPHAN wedged in (elev 0, NaN angle)
				Radial(8, 0.44f, "dv02", refl: true,  vel: true),   // SAILS base Doppler #2 (after the orphan)
				Radial(9, 1.76f, "hgh9", refl: true,  vel: true),   // higher tilt -> terminates dv02
			};

			var (data, complete, velComplete, _, _, _) = Level2Format.SelectLatestSweep(Header, blocks, Icao);

			Assert.True(complete);
			Assert.True(velComplete);                                  // the orphan-separated Doppler is a full sweep
			Assert.NotNull(data);
			Assert.True(SyntheticVolume.CountMarker(data!, "sv02") >= 1, "newer surveillance scan must be selected");
			Assert.True(SyntheticVolume.CountMarker(data!, "dv02") >= 1, "its Doppler companion must be paired across the orphan");
			Assert.Equal(0, SyntheticVolume.CountMarker(data!, "sv01")); // the older scan must NOT be what we serve
		}

		/// <summary>
		/// The over-skip guard: skipping orphans must NOT let the search leap over a genuine DIFFERENT tilt to
		/// grab a far-away Doppler. Here the newer base surveillance is followed immediately by a real 0.88°
		/// cut (no orphan), so it has NO companion and the selector must fall back to the older, properly-paired
		/// base scan.
		/// </summary>
		[Fact]
		public void DoesNotPairAcrossAGenuineDifferentTilt()
		{
			var blocks = new List<(byte[] block, int elev)>
			{
				Meta("lead"),
				Radial(1, 0.44f, "sv01", refl: true,  vel: false),  // base surveillance #1
				Radial(2, 0.44f, "dv01", refl: true,  vel: true),   // base Doppler #1 (adjacent, complete)
				Radial(7, 0.44f, "sv02", refl: true,  vel: false),  // newer base surveillance...
				Radial(8, 0.88f, "hgh8", refl: true,  vel: true),   // ...but a REAL 0.9° tilt follows (not a companion)
				Radial(9, 1.30f, "hgh9", refl: true,  vel: true),   // terminate
			};

			var (data, complete, velComplete, _, _, _) = Level2Format.SelectLatestSweep(Header, blocks, Icao);

			Assert.True(complete);
			Assert.True(velComplete);
			Assert.NotNull(data);
			Assert.True(SyntheticVolume.CountMarker(data!, "sv01") >= 1, "must fall back to the older paired scan");
			Assert.Equal(0, SyntheticVolume.CountMarker(data!, "sv02")); // newer scan had no companion -> not served
		}
	}
}
