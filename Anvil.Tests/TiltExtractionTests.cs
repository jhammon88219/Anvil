using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Tilt extraction — <c>Level2Format.TryExtractTiltByAngle</c>. This is the code TiltCheck exercises
	/// against live volumes; these tests pin the same decisions deterministically, offline, in
	/// milliseconds. Volumes are synthesized (see <see cref="SyntheticVolume"/>).
	/// </summary>
	public class TiltExtractionTests
	{
		private const string Site = SyntheticVolume.DefaultIcao;

		[Fact]
		public void ExtractsOnlyTheCutMatchingTheTargetAngle()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.5f, "aaaa"),
				SyntheticVolume.Radial(2, 1.5f, "bbbb"),
				SyntheticVolume.Radial(3, 2.4f, "cccc"));

			var tilt = Level2Format.TryExtractTiltByAngle(volume, Site, 1.5f, out _);

			Assert.NotNull(tilt);
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "bbbb"));
			Assert.Equal(0, SyntheticVolume.CountMarker(tilt!, "aaaa"));
			Assert.Equal(0, SyntheticVolume.CountMarker(tilt!, "cccc"));
		}

		/// <summary>
		/// ⚠️ REGRESSION TEST. A cut's radials report slightly different elevation angles, and one record
		/// can report a wildly high one. The cut must settle on the MEDIAN of those angles, not the max —
		/// taking the max made a 0.5° cut look like a 1.3° cut, so the target no longer matched and the
		/// extractor reported "tilt not in volume" for a tilt that was right there.
		/// </summary>
		[Fact]
		public void SettlesACutOnTheMedianOfItsRadialAngles()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.48f, "aaaa"),
				SyntheticVolume.Radial(1, 1.30f, "bbbb"),   // outlier: would poison a max/first-wins rule
				SyntheticVolume.Radial(1, 0.52f, "cccc"),
				SyntheticVolume.Radial(2, 2.40f, "dddd"));  // a later cut, so the target cut gets flushed

			var tilt = Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out _);

			// Median of [0.48, 0.52, 1.30] is 0.52 -> within TiltMatchTol (0.20) of the 0.5 target.
			// Had the cut settled on the 1.30 outlier, the match would fail and this would be null.
			Assert.NotNull(tilt);
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "aaaa"));
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "bbbb"));
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "cccc"));
		}

		[Fact]
		public void ReturnsNullWhenNoCutIsWithinTheMatchTolerance()
		{
			// 0.9 is 0.4 away from the 0.5 target — outside TiltMatchTol (0.20).
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.9f, "aaaa"),
				SyntheticVolume.Radial(2, 1.5f, "bbbb"));

			Assert.Null(Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out _));
		}

		[Fact]
		public void ReturnsNullForANaNTarget()
		{
			var volume = SyntheticVolume.Volume(SyntheticVolume.Radial(1, 0.5f, "aaaa"));

			Assert.Null(Level2Format.TryExtractTiltByAngle(volume, Site, float.NaN, out _));
		}

		/// <summary>
		/// A surveillance (reflectivity) cut is followed by its Doppler companion: a DIFFERENT elevation
		/// number at the SAME angle carrying velocity. Both belong to the tilt.
		/// </summary>
		[Fact]
		public void AttachesTheDopplerCompanionAtTheSameAngle()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.5f, "aaaa", reflectivity: true, velocity: false),
				SyntheticVolume.Radial(2, 0.5f, "bbbb", reflectivity: false, velocity: true),
				SyntheticVolume.Radial(3, 1.5f, "cccc"));

			var tilt = Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out _);

			Assert.NotNull(tilt);
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "aaaa"));
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "bbbb"));
			Assert.Equal(0, SyntheticVolume.CountMarker(tilt!, "cccc"));
		}

		/// <summary>A velocity cut at a DIFFERENT angle is the next tilt, not our companion.</summary>
		[Fact]
		public void DoesNotAttachAVelocityCutAtADifferentAngle()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.5f, "aaaa", reflectivity: true, velocity: false),
				SyntheticVolume.Radial(2, 1.5f, "bbbb", reflectivity: false, velocity: true),
				SyntheticVolume.Radial(3, 2.4f, "cccc"));

			var tilt = Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out _);

			Assert.NotNull(tilt);
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "aaaa"));
			Assert.Equal(0, SyntheticVolume.CountMarker(tilt!, "bbbb"));
		}

		/// <summary>
		/// completedTilt means "a LATER cut proved this one finished". Reaching EOF while still inside the
		/// target cut cannot prove that — a truncated prefix is indistinguishable from a short volume — so
		/// the data is still served, but not claimed complete.
		/// </summary>
		[Fact]
		public void DoesNotClaimCompletionWhenTheVolumeEndsInsideTheTargetCut()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.5f, "aaaa"));

			var tilt = Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out var completed);

			Assert.NotNull(tilt);                                   // served anyway
			Assert.False(completed);                                // but not claimed complete
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "aaaa"));
		}

		[Fact]
		public void ClaimsCompletionOnceALaterCutIsReached()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.5f, "aaaa"),
				SyntheticVolume.Radial(2, 1.5f, "bbbb"),
				SyntheticVolume.Radial(3, 2.4f, "cccc"));

			Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out var completed);

			Assert.True(completed);
		}

		/// <summary>Leading metadata records (VCP tables etc.) are what the JS decoder needs to make sense
		/// of the radials, so every extracted tilt carries them.</summary>
		[Fact]
		public void CarriesLeadingMetadataIntoTheExtractedTilt()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Metadata("mmmm"),
				SyntheticVolume.Radial(1, 0.5f, "aaaa"),
				SyntheticVolume.Radial(2, 1.5f, "bbbb"));

			var tilt = Level2Format.TryExtractTiltByAngle(volume, Site, 0.5f, out _);

			Assert.NotNull(tilt);
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "mmmm"));
		}

		/// <summary>
		/// Some radars write radials under a callsign that differs from their AWS bucket key — the ROC
		/// test bed KCRI writes "NOK5". Without the fallback the tilt walk never anchors and the whole
		/// volume gets cached as an unrenderable blob, so this path matters.
		/// </summary>
		[Fact]
		public void FallsBackToTheRadialsOwnIcaoWhenTheSiteIdIsAbsent()
		{
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Radial(1, 0.5f, "aaaa", icao: "NOK5"),
				SyntheticVolume.Radial(2, 1.5f, "bbbb", icao: "NOK5"),
				SyntheticVolume.Radial(3, 2.4f, "cccc", icao: "NOK5"));

			var tilt = Level2Format.TryExtractTiltByAngle(volume, "KCRI", 0.5f, out _);

			Assert.NotNull(tilt);
			Assert.Equal(1, SyntheticVolume.CountMarker(tilt!, "aaaa"));
			Assert.Equal(0, SyntheticVolume.CountMarker(tilt!, "bbbb"));
		}

		[Fact]
		public void ReturnsNullForATruncatedVolume()
		{
			Assert.Null(Level2Format.TryExtractTiltByAngle(new byte[8], Site, 0.5f, out _));
		}
	}
}
