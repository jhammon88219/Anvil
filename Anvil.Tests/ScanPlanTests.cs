using System;
using System.Collections.Generic;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="ScanPlan"/> — the planned scan read from Message 5, the input to regime-aware polling. The azimuth-rate
	/// decode must equal the vendored nexrad-level-2-data decoder's ("me"), and a cut's flags and speed must land where
	/// the 46-byte cut layout puts them.
	/// </summary>
	public class ScanPlanTests
	{
		// The vendored decoder's azimuth-rate function, transcribed: bits 14..3 each add 22.5 / 2^(14-r); bit 15 = sign.
		private static double VendorRate(int e)
		{
			double t = 0;
			for (var r = 14; r >= 3; r--) if ((e & (1 << r)) != 0) t += 22.5 / Math.Pow(2, 14 - r);
			return (e & (1 << 15)) != 0 ? -t : t;
		}

		[Fact]
		public void AzimuthRate_MatchesTheVendoredDecoder_ForEveryCode()
		{
			for (var raw = 0; raw <= 0xFFFF; raw++)
			{
				Assert.Equal(Math.Abs(VendorRate(raw)), ScanPlan.AzimuthRate(raw), 9);
			}
		}

		[Fact]
		public void TryRead_ReadsEachCutsAngleRateAndRescanFlags()
		{
			// One 2432-byte metadata record holding a Message 5 with two cuts: 0.5° (a SAILS cut, sequence 2) and 0.9° (MRLE).
			var block = new byte[Level2Format.RadarDataSize];
			block[Level2Format.CtmHeaderSize + 3] = 5;
			var body = Level2Format.CtmHeaderSize + Level2Format.MessageHeaderSize;
			block[body + 5] = 212;                                   // VCP 212
			block[body + 7] = 2;                                     // two cuts
			void Cut(int k, double angle, int rateCode, int supp)
			{
				var c = body + 22 + k * 46;
				var a = (short)Math.Round(angle / 0.043945 * 8);
				block[c] = (byte)(a >> 8); block[c + 1] = (byte)a;
				block[c + 3] = 1;
				block[c + 8] = (byte)(rateCode >> 8); block[c + 9] = (byte)rateCode;
				block[c + 28] = (byte)(supp >> 8); block[c + 29] = (byte)supp;
			}
			Cut(0, 0.5, 0x4000, 0b0101);  // 22.5°/s → 16 s; SAILS, sequence 2
			Cut(1, 0.9, 0x2000, 0b1_0000); // 11.25°/s → 32 s; MRLE

			Assert.True(ScanPlan.TryRead(new List<(byte[], int)> { (block, 0) }, out var vcp, out var cuts));
			Assert.Equal(212, vcp);
			Assert.Equal(2, cuts.Count);
			Assert.Equal(0.5, cuts[0].Angle, 2);
			Assert.Equal(16, cuts[0].SweepSeconds, 6);
			Assert.True(cuts[0].IsSails);
			Assert.Equal(2, cuts[0].SailsSequence);
			Assert.False(cuts[0].IsMrle);
			Assert.Equal(32, cuts[1].SweepSeconds, 6);
			Assert.True(cuts[1].IsMrle);
			Assert.False(cuts[1].IsSails);
		}
	}
}
