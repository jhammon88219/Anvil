using System;
using System.Collections.Generic;

namespace Anvil.Services
{
	/// <summary>
	/// The VCP's PLANNED scan, read from the volume's own Message 5: every cut in order with its antenna speed and its
	/// rescan flags — the input to REGIME-AWARE POLLING (predicting when the next sweep of a tilt finishes, instead of
	/// polling on a fixed interval). Step 1 of that work (2026-10-07) is MEASUREMENT: <c>TiltCheck -- --regime KTLX</c>
	/// prints this plan's predicted cut ends beside the real radial times and the chunks' arrival in the bucket.
	/// </summary>
	/// <remarks>
	/// Cut block (46 bytes, offsets from its start — the same layout as the vendored nexrad-level-2-data parser):
	/// +0 elevation angle (int16, value/8 × 0.043945°) · +3 waveform · +8 AZIMUTH RATE · +28 supplemental flags.
	/// ⚠️ Azimuth rate is the vendored decoder's "me": bits 14..3 are deg/s with bit 14 = 22.5 (so one step =
	/// 22.5/2048), bit 15 the sign. Supplemental ("be"): bit 0 = SAILS cut, bits 1-3 = its SAILS sequence, bit 4 = MRLE cut.
	/// A sweep lasts ≈ 360° ÷ rate; the gaps between cuts (antenna moves, settles) are what the measurement finds.
	/// </remarks>
	internal static class ScanPlan
	{
		internal sealed record Cut(int Number, double Angle, int Waveform, double AzimuthRate, bool IsSails, int SailsSequence, bool IsMrle)
		{
			/// <summary>One full rotation at the planned speed, seconds (0 when the rate is unreadable).</summary>
			public double SweepSeconds => AzimuthRate > 0 ? 360.0 / AzimuthRate : 0;
		}

		private const int CutsStart = 22, CutStride = 46;

		/// <summary>The planned cuts of the first Message 5 in <paramref name="blocks"/> (stops at the first radial).</summary>
		internal static bool TryRead(List<(byte[] block, int elev)> blocks, out int vcp, out List<Cut> cuts)
		{
			vcp = 0;
			cuts = new List<Cut>();
			foreach (var (block, _) in blocks)
			{
				for (var pos = 0; pos + Level2Format.CtmHeaderSize + Level2Format.MessageHeaderSize + 8 <= block.Length;
					pos += Level2Format.RadarDataSize)
				{
					var msgType = block[pos + Level2Format.CtmHeaderSize + 3];
					if (msgType == 31) return false; // the radials: Message 5 is behind us
					if (msgType is not (5 or 7)) continue;

					var body = pos + Level2Format.CtmHeaderSize + Level2Format.MessageHeaderSize;
					var number = (block[body + 4] << 8) | block[body + 5];
					var count = (short)((block[body + 6] << 8) | block[body + 7]);
					if (!Level2Format.IsUsableVcp(number) || count is <= 0 or > 40) continue;
					for (var k = 0; k < count; k++)
					{
						var c = body + CutsStart + k * CutStride;
						if (c + 30 > block.Length) return false; // truncated table: not a plan to trust
						var angle = (short)Hw(block, c) / 8.0 * 0.043945;
						var supp = Hw(block, c + 28);
						cuts.Add(new Cut(k + 1, angle, block[c + 3], AzimuthRate(Hw(block, c + 8)),
							(supp & 1) != 0, (supp >> 1) & 0b111, (supp & 0b1_0000) != 0));
					}
					vcp = number;
					return cuts.Count > 0;
				}
			}
			return false;
		}

		/// <summary>The vendored decoder's azimuth-rate decode: |deg/s| from the coded halfword.</summary>
		internal static double AzimuthRate(int raw) => ((raw >> 3) & 0xFFF) * (22.5 / 2048);

		private static int Hw(byte[] b, int at) => (b[at] << 8) | b[at + 1];
	}
}
