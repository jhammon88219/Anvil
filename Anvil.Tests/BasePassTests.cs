using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Every 0.5° PASS of a volume as its own frame — the SAILS / MRLE rescans the loop used to drop
	/// (<c>Level2Format.TryExtractBasePasses</c>, <c>PlannedBasePasses</c>, <see cref="RadarFrameKey"/>). Real-data proof
	/// is <c>TiltCheck -- --sails KVNX 2026/04/24 01:20 2</c> (4 passes per volume, pass 1 byte-identical to the
	/// volume-start extraction); these pin the rules on synthetic volumes.
	/// </summary>
	public class BasePassTests
	{
		// A SAILS ×1 split-cut volume: base surveillance + Doppler, a 0.9° tilt, the base RESCAN + its Doppler, a top tilt.
		private static byte[] SailsVolume() => SyntheticVolume.Volume(
			SyntheticVolume.Metadata("lead"),
			SyntheticVolume.Radial(1, 0.48f, "sv01"),
			SyntheticVolume.Radial(2, 0.48f, "dv01", velocity: true),
			SyntheticVolume.Radial(3, 0.88f, "hg03"),
			SyntheticVolume.Radial(4, 0.48f, "sv02"),
			SyntheticVolume.Radial(5, 0.48f, "dv02", velocity: true),
			SyntheticVolume.Radial(6, 1.32f, "hg06", velocity: true));

		[Fact]
		public void EveryBasePassBecomesItsOwnPairedFrame()
		{
			var passes = Level2Format.TryExtractBasePasses(SailsVolume(), SyntheticVolume.DefaultIcao);

			Assert.Equal(2, passes.Count);
			// Pass 1 = the volume start: its pair, no rescan.
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[0].Data, "sv01"));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[0].Data, "dv01"));
			Assert.Equal(0, SyntheticVolume.CountMarker(passes[0].Data, "sv02"));
			// Pass 2 = the rescan with ITS Doppler — and nothing of the other tilts.
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[1].Data, "sv02"));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[1].Data, "dv02"));
			Assert.Equal(0, SyntheticVolume.CountMarker(passes[1].Data, "sv01"));
			Assert.Equal(0, SyntheticVolume.CountMarker(passes[1].Data, "hg03"));
			// Both carry the leading metadata the decoder needs.
			Assert.All(passes, p => Assert.Equal(1, SyntheticVolume.CountMarker(p.Data, "lead")));
		}

		[Fact]
		public void PassOneIsTheVolumeStartExtraction()
		{
			// Pass 1 must be the bytes the loop has always shown for a volume (TiltCheck proves it byte-identical on real
			// volumes): the same surveillance + Doppler pair TryExtractLowestTilt cuts.
			var volume = SailsVolume();
			var legacy = Level2Format.TryExtractLowestTilt(volume, SyntheticVolume.DefaultIcao)!;
			var pass1 = Level2Format.TryExtractBasePasses(volume, SyntheticVolume.DefaultIcao)[0].Data;

			foreach (var marker in new[] { "lead", "sv01", "dv01", "sv02", "hg03" })
			{
				Assert.Equal(SyntheticVolume.CountMarker(legacy, marker), SyntheticVolume.CountMarker(pass1, marker));
			}
		}

		[Fact]
		public void ARescanAcrossAnOrphanCutStillPairs()
		{
			// The KDVN orphan (LiveSweepPairingTests): a 1-block NaN-angle cut wedged between a rescan and its Doppler.
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Metadata("lead"),
				SyntheticVolume.Radial(1, 0.48f, "sv01"),
				SyntheticVolume.Radial(2, 0.48f, "dv01", velocity: true),
				SyntheticVolume.Radial(7, 0.48f, "sv02"),
				SyntheticVolume.Metadata("orph"),
				SyntheticVolume.Radial(8, 0.48f, "dv02", velocity: true),
				SyntheticVolume.Radial(9, 1.76f, "hg09", velocity: true));

			var passes = Level2Format.TryExtractBasePasses(volume, SyntheticVolume.DefaultIcao);

			Assert.Equal(2, passes.Count);
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[1].Data, "dv02"));
		}

		[Fact]
		public void AnUnfinishedLastPassIsNotAFrame()
		{
			// A rescan whose Doppler is the volume's LAST cut with no end-of-elevation status (a truncated volume) is not
			// finished — the pass list stops at the last whole pair rather than serving a wedge.
			var volume = SyntheticVolume.Volume(
				SyntheticVolume.Metadata("lead"),
				SyntheticVolume.Radial(1, 0.48f, "sv01"),
				SyntheticVolume.Radial(2, 0.48f, "dv01", velocity: true),
				SyntheticVolume.Radial(3, 0.88f, "hg03"),
				SyntheticVolume.Radial(4, 0.48f, "sv02"));

			var passes = Level2Format.TryExtractBasePasses(volume, SyntheticVolume.DefaultIcao);

			Assert.Single(passes);
		}

		[Fact]
		public void A_jittered_volume_start_pass_is_still_pass_one_by_the_plan()
		{
			// KTLH 2026-10-10 05:38:38 (TiltCheck -- --sails KTLH 2026/10/10 05:38 1): the volume-start pass MEASURED 0.57°
			// and SAILS 1 0.44°, so "within 0.12° of the lowest measured angle" dropped pass 1 and SAILS 1 became #1 — every
			// later frame showed the next scan and the planned last pass never filled. The plan's numbers (#1, #5) decide.
			var volume = SyntheticVolume.Volume(
				Message5((0.5, 1), (0.5, 2), (0.9, 1), (0.9, 2), (0.5, 1), (0.5, 2), (1.3, 2)),
				SyntheticVolume.Radial(1, 0.57f, "sv01"),
				SyntheticVolume.Radial(2, 0.53f, "dv02", velocity: true),
				SyntheticVolume.Radial(3, 0.92f, "hg03"),
				SyntheticVolume.Radial(4, 0.92f, "hg04", velocity: true),
				SyntheticVolume.Radial(5, 0.44f, "sv05"),
				SyntheticVolume.Radial(6, 0.53f, "dv06", velocity: true),
				SyntheticVolume.Radial(7, 1.32f, "hg07", velocity: true));

			var passes = Level2Format.TryExtractBasePasses(volume, SyntheticVolume.DefaultIcao);

			Assert.Equal(new[] { 1, 2 }, passes.Select(p => p.Pass));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[0].Data, "sv01"));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[0].Data, "dv02"));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[1].Data, "sv05"));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[1].Data, "dv06"));
		}

		[Fact]
		public void A_planned_pass_the_volume_lacks_leaves_its_number_empty()
		{
			// Planned passes at #1, #4, #7; the volume never scanned #4/#5. The rescan at #7 is PASS 3 — it must not slide
			// into #2's frame (that frame comes back empty, honestly, instead of showing the wrong scan).
			var volume = SyntheticVolume.Volume(
				Message5((0.5, 1), (0.5, 2), (0.9, 1), (0.5, 1), (0.5, 2), (1.3, 1), (0.5, 1), (0.5, 2), (1.8, 2)),
				SyntheticVolume.Radial(1, 0.48f, "sv01"),
				SyntheticVolume.Radial(2, 0.48f, "dv02", velocity: true),
				SyntheticVolume.Radial(3, 0.88f, "hg03"),
				SyntheticVolume.Radial(6, 1.32f, "hg06"),
				SyntheticVolume.Radial(7, 0.48f, "sv07"),
				SyntheticVolume.Radial(8, 0.48f, "dv08", velocity: true),
				SyntheticVolume.Radial(9, 1.76f, "hg09", velocity: true));

			var passes = Level2Format.TryExtractBasePasses(volume, SyntheticVolume.DefaultIcao);

			Assert.Equal(new[] { 1, 3 }, passes.Select(p => p.Pass));
			Assert.Equal(1, SyntheticVolume.CountMarker(passes[1].Data, "sv07"));
		}

		[Fact]
		public void PlannedBasePassNumbers_AreThePlansCutNumbers()
		{
			var meta = Message5((0.5, 1), (0.5, 2), (0.9, 1), (0.5, 1), (0.5, 2), (1.3, 1), (0.5, 1), (0.5, 2));
			Assert.Equal(new[] { 1, 4, 7 }, Level2Format.PlannedBasePassNumbers(new List<(byte[], int)> { (meta, 0) }));
			Assert.Empty(Level2Format.PlannedBasePassNumbers(new List<(byte[], int)> { (new byte[64], 0) }));
		}

		[Fact]
		public void PlannedBasePasses_CountsNonDopplerCutsAtTheLowestAngle()
		{
			// VCP 212 + SAILS ×2: 0.5 CS, 0.5 CD, 0.9, 0.5 CS (SAILS 1), 0.5 CD, 1.3, 0.5 CS (SAILS 2), 0.5 CD.
			var meta = Message5((0.5, 1), (0.5, 2), (0.9, 1), (0.5, 1), (0.5, 2), (1.3, 1), (0.5, 1), (0.5, 2));
			Assert.Equal(3, Level2Format.PlannedBasePasses(new List<(byte[], int)> { (meta, 0) }));

			// Clear air: one base pass.
			var clear = Message5((0.5, 1), (0.5, 2), (1.5, 1), (1.5, 2));
			Assert.Equal(1, Level2Format.PlannedBasePasses(new List<(byte[], int)> { (clear, 0) }));

			// No readable table: one frame per volume, as before.
			Assert.Equal(1, Level2Format.PlannedBasePasses(new List<(byte[], int)> { (new byte[64], 0) }));
		}

		[Fact]
		public void PlannedBasePasses_DoesNotCountANearbyHigherTiltAsARescan()
		{
			// KEVX VCP 112 (2026-10-08): base 0.31°, a 0.44° tilt next, SAILS rescan back at the base. The 0.44° tilt is
			// within the old 0.25° window — it planned 3 passes for a volume that scans 2, and the phantom frames never
			// arrived, so the loop never filled. The extractor's 0.12° window is the rule.
			var meta = Message5((0.31, 1), (0.31, 2), (0.44, 1), (0.44, 2), (0.88, 1), (0.88, 2), (0.31, 1), (0.31, 2));
			Assert.Equal(2, Level2Format.PlannedBasePasses(new List<(byte[], int)> { (meta, 0) }));
		}

		[Fact]
		public void FrameKey_RoundTripsAndPassOneIsThePlainVolumeKey()
		{
			const string key = "2026/04/24/KVNX/KVNX20260424_011707_V06";
			Assert.Equal(key, RadarFrameKey.Of(key, 1));
			Assert.Equal(key + "#3", RadarFrameKey.Of(key, 3));
			Assert.Equal(key, RadarFrameKey.VolumeKey(key + "#3"));
			Assert.Equal(3, RadarFrameKey.Pass(key + "#3"));
			Assert.Equal(1, RadarFrameKey.Pass(key));

			var frames = RadarFrameKey.Expand(new[] { "a", "b", "c" }, new Dictionary<string, int> { ["a"] = 2, ["b"] = 1 });
			Assert.Equal(new[] { "a", "a#2", "b", "c" }, frames); // "c" has no count → one frame
		}

		// A metadata record holding a Message 5 with the given (angle, waveform) cuts.
		private static byte[] Message5(params (double angle, int waveform)[] cuts)
		{
			var block = new byte[Level2Format.RadarDataSize];
			block[Level2Format.CtmHeaderSize + 3] = 5;
			var body = Level2Format.CtmHeaderSize + Level2Format.MessageHeaderSize;
			block[body + 5] = 212;
			block[body + 7] = (byte)cuts.Length;
			for (var k = 0; k < cuts.Length; k++)
			{
				var c = body + 22 + k * 46;
				var a = (short)Math.Round(cuts[k].angle / 0.043945 * 8);
				block[c] = (byte)(a >> 8); block[c + 1] = (byte)a;
				block[c + 3] = (byte)cuts[k].waveform;
			}
			return block;
		}
	}
}
