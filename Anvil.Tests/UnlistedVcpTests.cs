using System;
using System.IO;
using System.Linq;
using Anvil.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// A pattern number VcpCatalog doesn't list is SHOWN (with its tilts) when the Message 5 table behind it is
	/// well-formed, and still rejected when it isn't. Built from the REAL VCP 212 fixture with its
	/// pattern_number rewritten — so the table is a genuine one and only the number is unfamiliar, exactly
	/// what a test pattern on KCRI looks like.
	/// </summary>
	public class UnlistedVcpTests
	{
		// 24-byte AR2V header + 12-byte CTM + 16-byte message header = the Message 5 body.
		private const int Body = 24 + 12 + 16;

		private static byte[] Vcp212() =>
			File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "level2-msg5-vcp212.bin"));

		private static byte[] WithVcp(byte[] bytes, int vcp)
		{
			Assert.Equal(212, (bytes[Body + 4] << 8) | bytes[Body + 5]); // the offset really is pattern_number
			bytes[Body + 4] = (byte)(vcp >> 8);
			bytes[Body + 5] = (byte)vcp;
			return bytes;
		}

		[Fact]
		public void AnUnlistedNumberWithAGoodTableIsShownWithItsTilts()
		{
			var tilt = WithVcp(Vcp212(), 301);
			var (vcp, sweeps) = Level2Format.ReadModeFromExtractedTilt(tilt);
			Assert.Equal(301, vcp);
			Assert.StartsWith("VCP 301 · unlisted", Level2Format.DescribeMode(vcp, sweeps));
			// The tilt picker keeps every tilt — the failure this replaces emptied it.
			Assert.Equal(Level2Format.ReadElevationAnglesFromExtractedTilt(Vcp212()),
				Level2Format.ReadElevationAnglesFromExtractedTilt(tilt));
		}

		[Fact]
		public void AnUnlistedNumberWithABadWaveformIsStillRejected()
		{
			var tilt = WithVcp(Vcp212(), 301);
			tilt[Body + 22 + 3] = 9; // first cut's waveform: no such code
			Assert.Equal(0, Level2Format.ReadModeFromExtractedTilt(tilt).vcp);
			Assert.Empty(Level2Format.ReadElevationAnglesFromExtractedTilt(tilt));
		}

		[Fact]
		public void AnOutOfRangeNumberIsRejected()
		{
			var tilt = WithVcp(Vcp212(), 900); // above the ICD's 767
			Assert.Equal(0, Level2Format.ReadModeFromExtractedTilt(tilt).vcp);
		}

		[Fact]
		public void AFailedParseStillReadsVcpQuestionMark() =>
			Assert.StartsWith("VCP ?", Level2Format.DescribeMode(0, 1));

		// ── the log ──

		private static (NonStandardVcpLog log, string dir) NewLog()
		{
			var dir = Path.Combine(Path.GetTempPath(), "anvil-vcplog-" + Guid.NewGuid().ToString("N"));
			return (new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, dir), dir);
		}

		[Fact]
		public void TheLogCountsDistinctFramesAndIgnoresCataloguedPatterns()
		{
			var (log, _) = NewLog();
			var t = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
			log.Record("KCRI", 301, t, new[] { 0.5f, 0.9f });
			log.Record("KCRI", 301, t.AddSeconds(20), null);  // same minute = the same frame re-read
			log.Record("KCRI", 301, t.AddMinutes(5), null);
			log.Record("KCRI", 212, t, null);                  // catalogued: not non-standard

			var s = Assert.Single(log.All);
			Assert.Equal(301, s.Vcp);
			Assert.Equal(2, s.Frames);
			Assert.Equal(t, s.FirstSeenUtc);
			Assert.Equal(t.AddMinutes(5), s.LastSeenUtc);
			Assert.Equal(new[] { 0.5f, 0.9f }, s.Tilts);
		}

		[Fact]
		public void TheLogSurvivesARestart()
		{
			var (log, dir) = NewLog();
			log.Record("KCRI", 301, DateTimeOffset.UtcNow, new[] { 0.5f });
			var reloaded = new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, dir);
			Assert.Equal(301, Assert.Single(reloaded.ForSite("kcri")).Vcp);
		}
	}
}
