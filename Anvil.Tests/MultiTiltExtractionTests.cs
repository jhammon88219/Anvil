using System.Collections.Generic;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Multi-tilt extraction — <c>Level2Format.TryExtractTiltsByAngles</c>, the single-decompression-pass
	/// extractor the storm-motion (VWP) path uses instead of calling <c>TryExtractTiltByAngle</c> once per
	/// angle (which re-decompressed the whole volume O(N²) times). The one-pass output must be BYTE-IDENTICAL
	/// to the per-angle reference for every angle — that equivalence is the guard: if the one-pass state
	/// machine diverges from the validated <c>TryExtractTiltByAngle</c>, the byte comparison fails.
	/// </summary>
	public class MultiTiltExtractionTests
	{
		private const string Site = SyntheticVolume.DefaultIcao;

		// A multi-cut volume: four surveillance cuts, two with a Doppler companion, one bare, plus higher
		// cuts above the requested range (to exercise the early exit). Leading metadata is carried into every
		// tilt. Angles chosen so each is a clean TiltMatchTol match.
		private static byte[] MultiCutVolume() => SyntheticVolume.Volume(
			SyntheticVolume.Metadata("mmmm"),
			SyntheticVolume.Radial(1, 0.50f, "a0re", reflectivity: true, velocity: false),
			SyntheticVolume.Radial(2, 0.50f, "a0ve", reflectivity: false, velocity: true),   // 0.5° Doppler companion
			SyntheticVolume.Radial(3, 1.50f, "b1re", reflectivity: true, velocity: false),
			SyntheticVolume.Radial(4, 1.50f, "b1ve", reflectivity: false, velocity: true),   // 1.5° Doppler companion
			SyntheticVolume.Radial(5, 2.40f, "c2re", reflectivity: true, velocity: false),   // 2.4° bare (no companion)
			SyntheticVolume.Radial(6, 3.40f, "d3re", reflectivity: true, velocity: false),
			SyntheticVolume.Radial(7, 3.40f, "d3ve", reflectivity: false, velocity: true),   // 3.4° Doppler companion
			SyntheticVolume.Radial(8, 5.00f, "e5re", reflectivity: true, velocity: false),   // above the requested set
			SyntheticVolume.Radial(9, 6.00f, "f6re", reflectivity: true, velocity: false));  // triggers the early exit

		/// <summary>
		/// ⚠️ THE core guarantee. For every requested angle, the one-pass buffer is byte-for-byte equal to
		/// what the per-angle <c>TryExtractTiltByAngle</c> produces — so the display tilt path can reuse these
		/// cache files and a bug in the one-pass walk shows up here as a byte mismatch.
		/// </summary>
		[Fact]
		public void MatchesThePerAngleExtractorByteForByteForEveryRequestedAngle()
		{
			var volume = MultiCutVolume();
			var angles = new List<float> { 0.50f, 1.50f, 2.40f, 3.40f };

			var multi = Level2Format.TryExtractTiltsByAngles(volume, Site, angles);

			foreach (var angle in angles)
			{
				var reference = Level2Format.TryExtractTiltByAngle(volume, Site, angle, out _);
				Assert.NotNull(reference);
				Assert.True(multi.TryGetValue(angle, out var onerun), $"angle {angle} missing from one-pass result");
				Assert.Equal(reference!, onerun); // byte-identical
			}
		}

		[Fact]
		public void ExtractsEveryRequestedCutInOnePass()
		{
			var volume = MultiCutVolume();

			var multi = Level2Format.TryExtractTiltsByAngles(volume, Site, new List<float> { 0.50f, 1.50f, 2.40f, 3.40f });

			Assert.Equal(4, multi.Count);
			// Spot-check the kept blocks: the 0.5° cut carries its refl + Doppler companion + shared metadata,
			// and none of another cut's markers.
			Assert.Equal(1, SyntheticVolume.CountMarker(multi[0.50f], "a0re"));
			Assert.Equal(1, SyntheticVolume.CountMarker(multi[0.50f], "a0ve"));
			Assert.Equal(1, SyntheticVolume.CountMarker(multi[0.50f], "mmmm"));
			Assert.Equal(0, SyntheticVolume.CountMarker(multi[0.50f], "b1re"));
			// The 2.4° cut is bare (its next group is a different angle), so no velocity block.
			Assert.Equal(1, SyntheticVolume.CountMarker(multi[2.40f], "c2re"));
			Assert.Equal(0, SyntheticVolume.CountMarker(multi[2.40f], "d3ve"));
		}

		[Fact]
		public void OmitsAnglesTheVolumeDoesNotContain()
		{
			var volume = MultiCutVolume();

			// 7.0° isn't in the volume; 1.5° is. Only the present one comes back.
			var multi = Level2Format.TryExtractTiltsByAngles(volume, Site, new List<float> { 1.50f, 7.00f });

			Assert.True(multi.ContainsKey(1.50f));
			Assert.False(multi.ContainsKey(7.00f));
			Assert.Null(Level2Format.TryExtractTiltByAngle(volume, Site, 7.00f, out _)); // reference agrees it's absent
		}

		[Fact]
		public void ReturnsEmptyForNoTargetsOrATruncatedVolume()
		{
			Assert.Empty(Level2Format.TryExtractTiltsByAngles(MultiCutVolume(), Site, new List<float>()));
			Assert.Empty(Level2Format.TryExtractTiltsByAngles(new byte[8], Site, new List<float> { 0.5f }));
		}

		/// <summary>The highest requested cut still gets its Doppler companion even though higher cuts follow
		/// it (a naive "stop at the top angle" would drop the companion, which is the very next group).</summary>
		[Fact]
		public void KeepsTheDopplerCompanionOfTheHighestRequestedCut()
		{
			var volume = MultiCutVolume();

			var multi = Level2Format.TryExtractTiltsByAngles(volume, Site, new List<float> { 3.40f });

			Assert.Equal(1, SyntheticVolume.CountMarker(multi[3.40f], "d3re"));
			Assert.Equal(1, SyntheticVolume.CountMarker(multi[3.40f], "d3ve")); // companion kept
			Assert.Equal(0, SyntheticVolume.CountMarker(multi[3.40f], "e5re")); // the next (higher) cut is not ours
		}
	}
}
