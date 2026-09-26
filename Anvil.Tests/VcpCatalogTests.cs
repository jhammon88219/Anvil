using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="VcpCatalog"/> is the one list of real scan patterns. These pin the facts other code leans
	/// on (the TDWR pair, the retired set PastCast still needs) and that the glossary's scale sentence is
	/// built from the catalog rather than hand-typed.
	/// </summary>
	public class VcpCatalogTests
	{
		[Fact]
		public void NumbersAreUnique() =>
			Assert.Equal(VcpCatalog.All.Count, VcpCatalog.All.Select(v => v.Number).Distinct().Count());

		[Fact]
		public void TdwrHasExactlyMonitorAndHazardous() =>
			Assert.Equal(new[] { 80, 90 },
				VcpCatalog.All.Where(v => v.Network == VcpNetwork.Tdwr).Select(v => v.Number).OrderBy(n => n));

		[Theory]
		[InlineData(11)]
		[InlineData(21)]
		[InlineData(32)]
		[InlineData(121)]
		[InlineData(211)]
		[InlineData(221)]
		public void RetiredPatternsAreFlagged(int vcp) => Assert.True(VcpCatalog.Find(vcp)!.Retired);

		[Fact]
		public void EveryPatternHasWords() =>
			Assert.All(VcpCatalog.All, v =>
			{
				Assert.False(string.IsNullOrWhiteSpace(v.Name));
				Assert.False(string.IsNullOrWhiteSpace(v.Summary));
			});

		[Fact]
		public void GlossaryScaleListsTheOperationalSetFromTheCatalog()
		{
			var context = RadarGlossary.ScanPattern("VCP 35 · clear-air").Context;
			Assert.Contains("Precipitation: 12, 212, 112, 215.", context);
			Assert.Contains("Clear-air: 35, 31, 34.", context);   // 34 was missing from the hand-typed line
			Assert.DoesNotContain("32", context);                 // retired, not advertised
		}

		[Fact]
		public void TdwrScaleNamesTheTwoTerminalModes() =>
			Assert.Contains("90 (monitor) and 80 (hazardous)", RadarGlossary.ScanPattern("VCP 80 · TDWR hazardous").Context);
	}
}
