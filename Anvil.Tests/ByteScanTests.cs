using System.Text;
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
