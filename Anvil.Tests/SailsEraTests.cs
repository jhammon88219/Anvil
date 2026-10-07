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
		[InlineData(212, 1, false, "VCP 212 · precip · pre-SAILS · 0.5°×1")]
		[InlineData(212, 1, true, "VCP 212 · precip · SAILS off · 0.5°×1")]
		[InlineData(212, 1, null, "VCP 212 · precip · 0.5°×1")]
		[InlineData(212, 3, true, "VCP 212 · precip · SAILS/MRLE ×2 · 0.5°×3")]
		[InlineData(80, 1, false, "VCP 80 · TDWR hazardous · 0.5°×1")] // TDWR never gets SAILS words
		public void DescribeModeSaysWhyThereAreNoExtraSweeps(int vcp, int sweeps, bool? existed, string expected) =>
			Assert.Equal(expected, Level2Format.DescribeMode(vcp, sweeps, existed));

		[Fact]
		public void TheScanCutKeepsTheReason() =>
			Assert.Equal("VCP 212 · precip · pre-SAILS",
				RadarViewModel.ScanStrategyText(Level2Format.DescribeMode(212, 1, false)));

		[Fact]
		public void TheTooltipExplainsEachCase()
		{
			Assert.Contains("Pre-SAILS:", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 1, false)));
			Assert.Contains("SAILS off:", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 1, true)));
			Assert.DoesNotContain("SAILS", RadarGlossary.ScanPatternTooltip(Level2Format.DescribeMode(212, 1, null)));
		}
	}
}
