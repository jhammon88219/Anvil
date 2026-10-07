using System;
using System.Collections.Generic;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// One base sweep says WHY: "pre-SAILS" (the radar's RDA build predates SAILS, Build 14) vs "SAILS off" (it had
	/// SAILS, the office ran without it). The build comes from Message 2; real values seen in TiltCheck
	/// (2026-10-07): KTLX 2013-05-31 = 13.2, 2014-03-15 = 13.3, 2014-06-03 = 14, 2016 = 16.1, 2019 = 18.1, 2024 = 22.1.
	/// </summary>
	public class SailsEraTests
	{
		// A metadata block holding one Message 2 whose rda_build_number (body halfword 10) is `raw`.
		private static byte[] Message2Block(short raw, bool radialFirst = false)
		{
			var block = new byte[Level2Format.RadarDataSize * 2];
			var msg2 = radialFirst ? Level2Format.RadarDataSize : 0;
			if (radialFirst)
			{
				block[Level2Format.CtmHeaderSize + 3] = 31; // a radial before it ends the metadata walk
			}
			block[msg2 + Level2Format.CtmHeaderSize + 3] = 2;
			var body = msg2 + Level2Format.CtmHeaderSize + Level2Format.MessageHeaderSize;
			block[body + 18] = (byte)(raw >> 8);
			block[body + 19] = (byte)raw;
			return block;
		}

		private static double Build(byte[] block) =>
			Level2Format.ReadRdaBuildFromMetadata(new List<(byte[] block, int elev)> { (block, 0) });

		[Theory]
		[InlineData(132, 13.2)]   // old ×10 encoding
		[InlineData(1320, 13.2)]  // newer ×100 encoding
		[InlineData(140, 14.0)]
		[InlineData(1400, 14.0)]
		[InlineData(2210, 22.1)]
		[InlineData(0, 0.0)]      // no plausible build
		[InlineData(-5, 0.0)]
		public void ReadsTheBuildInBothEncodings(short raw, double expected) =>
			Assert.Equal(expected, Build(Message2Block(raw)), 3);

		[Fact]
		public void IgnoresAMessage2PastTheFirstRadial() =>
			Assert.Equal(0.0, Build(Message2Block(1400, radialFirst: true)));

		[Theory]
		[InlineData(13.2, 2013, 5, false)]  // El Reno
		[InlineData(13.3, 2014, 3, false)]  // the rollout: a 2014 date alone would be wrong here
		[InlineData(14.0, 2014, 6, true)]
		[InlineData(22.1, 2024, 5, true)]
		[InlineData(0.0, 2013, 5, false)]   // no build: before 2014 is certain
		[InlineData(0.0, 2016, 5, null)]    // no build after that: unknown
		public void SailsExistedFromBuildThenDate(double build, int year, int month, bool? expected) =>
			Assert.Equal(expected, Level2Format.SailsExisted(build, new DateTimeOffset(year, month, 15, 0, 0, 0, TimeSpan.Zero)));

		[Theory]
		[InlineData(212, 1, false, 0, "VCP 212 · precip · pre-SAILS · 0.5°×1")]
		[InlineData(212, 1, true, 0, "VCP 212 · precip · rescans off · 0.5°×1")]
		[InlineData(212, 1, null, 0, "VCP 212 · precip · 0.5°×1")]
		[InlineData(212, 2, true, 0, "VCP 212 · precip · SAILS ×1 · 0.5°×2")]
		[InlineData(212, 3, true, 0, "VCP 212 · precip · MESO-SAILS ×2 · 0.5°×3")]
		[InlineData(212, 4, true, 0, "VCP 212 · precip · MESO-SAILS ×3 · 0.5°×4")]
		[InlineData(35, 2, true, 0, "VCP 35 · clear-air · SAILS ×1 · 0.5°×2")]      // clear-air runs SAILS ×1 too
		[InlineData(212, 2, true, 3, "VCP 212 · precip · MRLE ×3 · 0.5°×2")]        // MRLE repeats 0.5° once — NOT SAILS ×1
		[InlineData(212, 2, true, 4, "VCP 212 · precip · MRLE ×4 · 0.5°×2")]
		[InlineData(80, 1, false, 0, "VCP 80 · TDWR hazardous · 0.5°×1")]          // TDWR never gets rescan words
		public void DescribeModeNamesTheRescanScheme(int vcp, int sweeps, bool? existed, int mrle, string expected) =>
			Assert.Equal(expected, Level2Format.DescribeMode(vcp, sweeps, existed, mrle));

		// A metadata block holding one Message 5 for `vcp` whose VCP SUPPLEMENTAL word (header halfword 10) is `word`.
		private static byte[] Message5Block(int vcp, int word)
		{
			var block = new byte[Level2Format.RadarDataSize];
			block[Level2Format.CtmHeaderSize + 3] = 5;
			var body = Level2Format.CtmHeaderSize + Level2Format.MessageHeaderSize;
			block[body + 4] = (byte)(vcp >> 8);
			block[body + 5] = (byte)vcp;
			block[body + 18] = (byte)(word >> 8);
			block[body + 19] = (byte)word;
			return block;
		}

		// Words seen in real volumes (TiltCheck --supp, 2026-10-07): KLIX 2022-03-22 0090, KLSX/KILX/KMKX 0070,
		// KIWX/KOHX 0050, SAILS 0003/0005/0007, KSHV base tilt 5000/5003, KHGX VCP 112 0803, builds 16-17 0000.
		[Theory]
		[InlineData(0x0090, 4)]
		[InlineData(0x0070, 3)]
		[InlineData(0x0050, 2)]
		[InlineData(0x0003, 0)]
		[InlineData(0x0007, 0)]
		[InlineData(0x5000, 0)]
		[InlineData(0x5003, 0)]
		[InlineData(0x0803, 0)]
		[InlineData(0x0000, 0)]
		[InlineData(0x0071, 0)] // MRLE + SAILS together can't happen — a misread, not a scheme
		[InlineData(0x0010, 0)] // MRLE with no tilt count
		public void ReadsMrleTiltsFromTheSupplementalWord(int word, int expected) =>
			Assert.Equal(expected, Level2Format.ReadMrleTiltsFromMetadata(
				new List<(byte[] block, int elev)> { (Message5Block(212, word), 0) }));

		[Fact]
		public void TheScanCutKeepsTheReason() =>
			Assert.Equal("VCP 212 · precip · pre-SAILS",
				RadarViewModel.ScanStrategyText(Level2Format.DescribeMode(212, 1, false)));

		[Fact]
		public void TheTooltipExplainsEachCase()
		{
			Assert.Contains("Pre-SAILS:", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 1, false)));
			Assert.Contains("Rescans off:", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 1, true)));
			Assert.Contains("SAILS ×1:", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 2, true)));
			Assert.Contains("MESO-SAILS ×3:", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 4, true)));
			var mrle = RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 2, true, 4));
			Assert.Contains("MRLE ×4: the lowest 4 tilts", mrle);
			Assert.DoesNotContain("SAILS ×", mrle);
			Assert.DoesNotContain("SAILS", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 1, null)));
		}
	}
}
