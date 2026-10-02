using System;
using System.IO;
using System.Linq;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="SpcRiskTableParser"/> + the window's risk cards, against REAL SPC outlook pages
	/// (Fixtures/spc-page-*.html, fetched 2026-10-02): May 6 2024 1630Z has all five categories; Mar 14 2025 20Z has
	/// no High; Apr 27 2011 1630Z predates the table.
	/// </summary>
	public class SpcRiskTableTests
	{
		private static string Page(string name) =>
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spc-page-{name}.html"));

		private static string Discussion(string name) =>
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spc-disc-{name}.txt"));

		[Fact]
		public void FullDay_ReadsAllFiveCategoricalRows_NotTheProbabilityTables()
		{
			var rows = SpcRiskTableParser.Parse(Page("20240506_1630"));
			Assert.Equal(new[] { "HIGH", "MDT", "ENH", "SLGT", "MRGL" }, rows.Select(r => r.Code));
			var high = rows[0];
			Assert.Equal(23_101, high.AreaSqMi);
			Assert.Equal(1_896_303, high.Population);
			Assert.Equal(new[] { "Oklahoma City, OK", "Norman, OK", "Edmond, OK", "Midwest City, OK", "Moore, OK" }, high.Places);
			Assert.Equal(31_343_171, rows[4].Population);
		}

		[Fact]
		public void NoHighDay_HasFourRows()
		{
			var rows = SpcRiskTableParser.Parse(Page("20250314_2000"));
			Assert.Equal(new[] { "MDT", "ENH", "SLGT", "MRGL" }, rows.Select(r => r.Code));
		}

		[Fact]
		public void OldPage_HasNoTable()
		{
			Assert.Empty(SpcRiskTableParser.Parse(Page("20110427_1630")));
			Assert.Empty(SpcRiskTableParser.Parse(null));
		}

		[Fact]
		public void Cards_AreAlwaysFive_HighToMarginal_AbsentDimmed()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.Load(Discussion("d1_20250314_2000"), riskAreas: SpcRiskTableParser.Parse(Page("20250314_2000")));
			Assert.True(vm.HasRiskCards);
			Assert.False(vm.HighCard.IsPresent);           // no High that day → dimmed
			Assert.True(vm.ModerateCard.HasFigures);
			Assert.Equal("13.0M", vm.ModerateCard.People);
			Assert.Equal("171,638 sq mi", vm.ModerateCard.Area);
			Assert.Equal("Memphis, TN", vm.ModerateCard.Places[0]);
			Assert.Equal("MARGINAL", vm.MarginalCard.Name);
		}

		// Without a table (an older page), presence comes from the headlines and the cards carry no figures.
		[Fact]
		public void NoTable_PresenceFromHeadlines_NoFigures()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.Load(Discussion("d1_20110427_1630"));
			Assert.True(vm.HighCard.IsPresentWithoutFigures);
			Assert.True(vm.SlightCard.IsPresent);
			Assert.False(vm.EnhancedCard.IsPresent);        // no Enhanced category in 2011
			Assert.False(vm.MarginalCard.IsPresent);
		}

		[Theory]
		[InlineData(1_896_303, "1.9M")]
		[InlineData(31_343_171, "31.3M")]
		[InlineData(280_571, "281K")]
		[InlineData(950, "950")]
		public void People_AreCompact(long n, string expected) => Assert.Equal(expected, OutlookRiskCard.Compact(n));
	}
}
