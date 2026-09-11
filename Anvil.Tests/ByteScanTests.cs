using System.Text;
using System;
using System.IO;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The low-level scanners the tilt walk is built on. These are cheap to get subtly wrong and their
	/// failures surface far away — a bad <c>HasMoment</c> makes a metadata record look like a radial, and
	/// the whole cut grouping goes with it.
	/// </summary>
	public class ByteScanTests
	{
		private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

		[Theory]
		[InlineData("hello world", "hello", 0)]      // at the start
		[InlineData("hello world", "world", 6)]      // at the very end
		[InlineData("hello world", "o w", 4)]        // spanning
		[InlineData("hello world", "xyz", -1)]       // absent
		[InlineData("aaa", "aaaa", -1)]              // needle longer than haystack
		[InlineData("aaaa", "aa", 0)]                // overlapping -> first match wins
		public void IndexOfFindsTheFirstMatch(string haystack, string needle, int expected)
		{
			Assert.Equal(expected, Level2Format.IndexOf(Ascii(haystack), Ascii(needle)));
		}

		[Fact]
		public void HasMomentAcceptsAPlausibleGateCount()
		{
			var block = new byte[32];
			Ascii("DREF").CopyTo(block, 4);
			block[4 + 8] = 0x07; block[4 + 9] = 0x28;   // 1832 gates

			Assert.True(Level2Format.HasMoment(block, Level2Format.Dref));
		}

		/// <summary>The gate-count range check is what rejects a coincidental "DREF" inside binary data —
		/// without it, non-moment records get mistaken for reflectivity radials.</summary>
		[Theory]
		[InlineData(0, 0)]          // zero gates
		[InlineData(0x7F, 0xFF)]    // 32767 gates — far past the 2000 ceiling
		public void HasMomentRejectsAnImplausibleGateCount(byte hi, byte lo)
		{
			var block = new byte[32];
			Ascii("DREF").CopyTo(block, 4);
			block[4 + 8] = hi; block[4 + 9] = lo;

			Assert.False(Level2Format.HasMoment(block, Level2Format.Dref));
		}

		[Fact]
		public void HasMomentRejectsANameTooCloseToTheEndToCarryAGateCount()
		{
			var block = new byte[8];
			Ascii("DREF").CopyTo(block, 4);   // name present, but no room for the count at +8

			Assert.False(Level2Format.HasMoment(block, Level2Format.Dref));
		}

		[Theory]
		[InlineData(1, 1)]
		[InlineData(32, 32)]
		[InlineData(0, 0)]      // 0 is not a valid 1-based elevation index
		[InlineData(33, 0)]     // past the plausible ceiling -> "not a radial"
		[InlineData(200, 0)]
		public void ElevationOfRejectsImplausibleElevationNumbers(byte written, int expected)
		{
			// A false ICAO match inside metadata yields a garbage byte here; reporting it as a tilt
			// boundary would split cuts at random, so anything out of range must read as 0.
			var block = new byte[32];
			Ascii("KTLX").CopyTo(block, 0);
			block[22] = written;

			Assert.Equal(expected, Level2Format.ElevationOf(block, Ascii("KTLX")));
		}

		[Fact]
		public void ElevationOfReturnsZeroWhenTheIcaoIsAbsent()
		{
			Assert.Equal(0, Level2Format.ElevationOf(new byte[32], Ascii("KTLX")));
		}

		[Fact]
		public void KnownVcpsCoverTheClearAirPrecipAndTdwrFamilies()
		{
			Assert.True(Level2Format.IsKnownVcp(31));    // clear air
			Assert.True(Level2Format.IsKnownVcp(12));    // precip
			Assert.True(Level2Format.IsKnownVcp(80));    // TDWR
			Assert.False(Level2Format.IsKnownVcp(999));
		}

		/// <summary>
		/// Every CURRENTLY DEPLOYED WSR-88D pattern must be recognised. ⚠️ This is not a tautology over a
		/// constant: an unknown VCP fails the <c>IsKnownVcp</c> gate inside
		/// <c>TryReadElevationTable</c>, which then returns an EMPTY elevation table — so the tilt picker
		/// offers nothing and the readout says "VCP ?". That has now shipped TWICE, for TDWR 80 and again
		/// for clear-air 34, because the set is a hand-maintained allow-list. This test is what makes a
		/// third recurrence fail loudly instead of silently emptying a picker on a real site.
		/// </summary>
		[Theory]
		[InlineData(12)]    // precip, SZ-2
		[InlineData(112)]   // precip, SZ-2 long-range
		[InlineData(212)]   // precip, SZ-2
		[InlineData(215)]   // precip, general surveillance
		[InlineData(31)]    // clear air, long pulse
		[InlineData(34)]    // clear air, SZ-2 — was MISSING, emptied the tilt list on these sites
		[InlineData(35)]    // clear air, SZ-2
		public void EveryDeployedVcpIsRecognised(int vcp)
		{
			Assert.True(Level2Format.IsKnownVcp(vcp), $"VCP {vcp} is deployed but unknown — the tilt list "
				+ "will come back empty on any site running it.");
		}

		// ── Message 5 elevation-table parse, against REAL BYTES ────────────────────────────────────
		// ⚠️ Each fixture is the 24-byte AR2V header + the single Message 5 record lifted out of a
		// committed corpus volume. Synthetic bytes would only confirm our own reading of the format —
		// the same reasoning that put a real Level III product in Fixtures/. They are ~2.4 KB because
		// the table walk checks every record's type, so a one-record block is found on the first stride.
		//
		// What this pins: the OFFSETS AND STRIDE (pattern_number at body+4, num_elevations at body+6, a
		// 22-byte header, 46-byte per-cut blocks, angle = raw/8*0.043945) and the collapsing rule that
		// turns designed cuts into the tilt list the picker shows. ⚠️ Those offsets were verified against
		// other open-source parsers, NOT a verbatim ICD table — the commissioned research could not
		// extract it. Real bytes with a known answer are the strongest check available here, and a stride
		// or encoding regression moves these angles immediately.
		// ⚠️ THREE PATTERNS ON PURPOSE: clear-air 35 (9 tilts), precip 212 (14) and precip 215 (15). One
		// VCP would not catch a stride bug that happens to land right on a 14-cut table.
		private static byte[] Fixture(string name) =>
			File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

		[Theory]
		[InlineData("level2-msg5-vcp35.bin", 35)]
		[InlineData("level2-msg5-vcp212.bin", 212)]
		[InlineData("level2-msg5-vcp215.bin", 215)]
		public void ScanModeParsesFromRealVolumes(string fixture, int expectedVcp)
		{
			var (vcp, _) = Level2Format.ReadModeFromExtractedTilt(Fixture(fixture));
			Assert.Equal(expectedVcp, vcp);
			Assert.True(Level2Format.IsKnownVcp(vcp));
		}

		[Fact]
		public void ElevationTableParsesFromARealClearAirVolume()
		{
			// VCP 35's 14 designed cuts collapse to 9 distinct tilts: the split cuts repeat an angle.
			var angles = Level2Format.ReadElevationAnglesFromExtractedTilt(Fixture("level2-msg5-vcp35.bin"));
			Assert.Equal(new[] { 0.31f, 0.88f, 1.27f, 1.8f, 2.42f, 3.08f, 4.0f, 5.1f, 6.42f }, angles);
		}

		[Fact]
		public void ElevationTableParsesFromARealVcp212Volume()
		{
			// 19 designed cuts -> 14 distinct: the split cuts AND the SAILS re-scan each repeat an angle
			// already in the list, which is exactly what ReadElevationAngles exists to collapse.
			var angles = Level2Format.ReadElevationAnglesFromExtractedTilt(Fixture("level2-msg5-vcp212.bin"));
			Assert.Equal(new[] { 0.48f, 0.88f, 1.27f, 1.8f, 2.42f, 3.08f, 4.0f, 5.1f, 6.42f,
								 8.0f, 10.02f, 12.48f, 15.6f, 19.51f }, angles);
		}

		[Fact]
		public void ElevationTableParsesFromARealVcp215Volume()
		{
			// 215 differs from 212 in the UPPER tilts (12.0/14.02/16.7 against 12.48/15.6), which is what
			// makes it worth a second precip fixture rather than a duplicate.
			var angles = Level2Format.ReadElevationAnglesFromExtractedTilt(Fixture("level2-msg5-vcp215.bin"));
			Assert.Equal(new[] { 0.48f, 0.88f, 1.27f, 1.8f, 2.42f, 3.08f, 4.0f, 5.1f, 6.42f,
								 8.0f, 10.02f, 12.0f, 14.02f, 16.7f, 19.51f }, angles);
		}

		/// <summary>
		/// Retired patterns stay recognised ON PURPOSE: the archive reaches back to 1991 and PastCast
		/// replays it, so a 2005 volume on VCP 11 is a legitimate read, not a bad parse.
		/// </summary>
		[Theory]
		[InlineData(11)]
		[InlineData(21)]
		[InlineData(121)]
		[InlineData(211)]
		[InlineData(221)]
		public void RetiredVcpsStayRecognisedForTheArchive(int vcp)
		{
			Assert.True(Level2Format.IsKnownVcp(vcp), $"VCP {vcp} is retired but still appears in archive "
				+ "volumes PastCast can replay.");
		}
	}
}
