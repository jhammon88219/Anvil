using System.Linq;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The temporal pane's pure rules: its two states (<see cref="TemporalPaneStates"/>), the docked set and dock order
	/// (<see cref="TemporalPaneDock"/>) and the rail-state height sharing (<see cref="DockStackAllocator"/>).
	/// </summary>
	public class TemporalPaneTests
	{
		// ── TemporalPaneStates ───────────────────────────────────────────────

		[Theory]
		[InlineData(null, "full")]
		[InlineData("", "full")]
		[InlineData("Rail", "full")]
		[InlineData("rail", "rail")]
		[InlineData("full", "full")]
		public void States_NormalizeUnknownToFull(string? token, string expected) =>
			Assert.Equal(expected, TemporalPaneStates.Normalize(token));

		[Fact]
		public void States_NextFlips()
		{
			Assert.Equal(TemporalPaneStates.Rail, TemporalPaneStates.Next(TemporalPaneStates.Full));
			Assert.Equal(TemporalPaneStates.Full, TemporalPaneStates.Next(TemporalPaneStates.Rail));
			Assert.Equal(TemporalPaneStates.Rail, TemporalPaneStates.Next(null));
			Assert.True(TemporalPaneStates.IsRail("rail"));
			Assert.False(TemporalPaneStates.IsRail("bogus"));
		}

		// ── TemporalPaneDock ─────────────────────────────────────────────────

		[Fact]
		public void FullOrder_HeaderFirstThenOwnLayersInTheUsersOrder()
		{
			var order = TemporalPaneDock.FullOrder(
				new[] { "radar", "warnings", "cells", "outlook", "reports", "watches", "tropical", "mds", "models", "damage" },
				TemporalPaneDock.NowCastLayers);
			// outlook / models / damage aren't NowCast's own sections (ghosts or another mode's).
			Assert.Equal(new[] { "header", "radar", "warnings", "cells", "reports", "watches", "tropical", "mds" }, order);
		}

		[Fact]
		public void FullOrder_FillsAnUnnamedOwnLayerWhereTheMapDrawsIt()
		{
			var order = TemporalPaneDock.FullOrder(new[] { "models" }, TemporalPaneDock.ForeCastLayers);
			Assert.Equal(new[] { "header", "outlook", "models" }, order);
			Assert.Equal(new[] { "header", "models", "outlook" }, TemporalPaneDock.FullOrder(new[] { "models", "outlook" }, TemporalPaneDock.ForeCastLayers));
		}

		[Fact]
		public void FullOrder_EmptyIsTheDefaultOrder()
		{
			var order = TemporalPaneDock.FullOrder(null, TemporalPaneDock.PastCastLayers);
			Assert.Equal("header", order[0]);
			Assert.Equal(LayerOrder.Default.Where(TemporalPaneDock.PastCastLayers.Contains), order.Skip(1));
		}

		[Fact]
		public void DockOrder_KeepsFullOrderAndClosesTheGaps()
		{
			var full = new[] { "header", "cells", "reports", "warnings", "watches", "radar" };
			var stack = TemporalPaneDock.DockOrder(full, new[] { "radar", "cells", "header" });
			Assert.Equal(new[] { "header", "cells", "radar" }, stack);
		}

		[Fact]
		public void DockOrder_NothingDockedIsEmpty() =>
			Assert.Empty(TemporalPaneDock.DockOrder(new[] { "header", "radar" }, null));

		[Fact]
		public void NormalizeDocked_DropsUnknownForeignAndRepeatedIds()
		{
			var docked = TemporalPaneDock.NormalizeDocked(
				new[] { "radar", "damage", "lightning", "header", "radar", "Header" },
				TemporalPaneDock.NowCastLayers);
			Assert.Equal(new[] { "radar", "header" }, docked);
		}

		[Fact]
		public void ToggleDocked_AddsThenRemoves()
		{
			var once = TemporalPaneDock.ToggleDocked(new[] { "header" }, "radar");
			Assert.Equal(new[] { "header", "radar" }, once);
			Assert.Equal(new[] { "header" }, TemporalPaneDock.ToggleDocked(once, "radar"));
			Assert.Equal(new[] { "cells" }, TemporalPaneDock.ToggleDocked(null, "cells"));
		}

		// ── DockStackAllocator ───────────────────────────────────────────────

		private static DockSectionSize ListSection(int rows, double rowHeight = 20, double min = 20, double more = 20, int cap = 0) =>
			new(28, true, min, rows, rowHeight, cap, more);

		[Fact]
		public void Fit_EverythingFitsAtNaturalHeight()
		{
			var fit = DockStackAllocator.Fit(new[]
			{
				new DockSectionSize(100, false, 0),        // the header block
				new DockSectionSize(28, true, 60),         // controls only
				ListSection(rows: 5),                      // 20 + 5×20
			}, available: 1000);

			Assert.False(fit.NeedsScroll);
			Assert.Equal(new[] { 0.0, 60, 120 }, fit.Sections.Select(s => s.BodyHeight));
			Assert.Equal(5, fit.Sections[2].VisibleRows);
			Assert.Equal(0, fit.Sections[2].HiddenRows);
			Assert.Equal(100 + 28 + 60 + 28 + 120, fit.Height);
		}

		[Fact]
		public void Fit_CollapsedSectionIsItsHeaderOnly()
		{
			var fit = DockStackAllocator.Fit(new[] { new DockSectionSize(28, false, 60, 10, 20) }, available: 1000);
			Assert.Equal(new DockSectionFit(0, 0, 0), fit.Sections[0]);
			Assert.Equal(28, fit.Height);
		}

		[Fact]
		public void Fit_ShortBodyKeepsItsHeightAndTheListsShareTheRest()
		{
			// Headers 3×28 = 84; A's body 50 never shrinks; B and C split the remaining 200 → 100 each:
			// 20 controls + 3 rows + the "+N more" line.
			var fit = DockStackAllocator.Fit(new[]
			{
				new DockSectionSize(28, true, 50),
				ListSection(rows: 20),
				ListSection(rows: 20),
			}, available: 84 + 50 + 200);

			Assert.False(fit.NeedsScroll);
			Assert.Equal(50, fit.Sections[0].BodyHeight);
			Assert.All(fit.Sections.Skip(1), s =>
			{
				Assert.Equal(100, s.BodyHeight);
				Assert.Equal(3, s.VisibleRows);
				Assert.Equal(17, s.HiddenRows);
			});
			Assert.Equal(334, fit.Height);
		}

		[Fact]
		public void Fit_ASmallListKeepsItsRowsAndGivesTheRestAway()
		{
			// B needs only 20 + 2×20 = 60 of its 100 share; C takes the other 40 → 140 = 20 + 5 rows + more.
			var fit = DockStackAllocator.Fit(new[]
			{
				new DockSectionSize(28, true, 50),
				ListSection(rows: 2),
				ListSection(rows: 20),
			}, available: 84 + 50 + 200);

			Assert.Equal(60, fit.Sections[1].BodyHeight);
			Assert.Equal(0, fit.Sections[1].HiddenRows);
			Assert.Equal(140, fit.Sections[2].BodyHeight);
			Assert.Equal(5, fit.Sections[2].VisibleRows);
		}

		[Fact]
		public void Fit_WholeRowLeftoverIsHandedBackRoundRobin()
		{
			// 100 for two lists with no controls or more line; level 50 → 2 rows of 20 + 1 row of 30 = 70. The 30 left
			// gives B a third row (90); a fourth (110) or C's second (120) no longer fits.
			var fit = DockStackAllocator.Fit(new[]
			{
				new DockSectionSize(0, true, 0, 10, 20),
				new DockSectionSize(0, true, 0, 10, 30),
			}, available: 100);

			Assert.Equal(3, fit.Sections[0].VisibleRows);
			Assert.Equal(1, fit.Sections[1].VisibleRows);
			Assert.True(fit.Height <= 100);
		}

		[Fact]
		public void Fit_MinimumsThatDontFitScroll()
		{
			var fit = DockStackAllocator.Fit(new[]
			{
				new DockSectionSize(28, true, 80),
				ListSection(rows: 10),
			}, available: 150);

			Assert.True(fit.NeedsScroll);
			Assert.Equal(80, fit.Sections[0].BodyHeight);
			Assert.Equal(40, fit.Sections[1].BodyHeight);   // controls + "+10 more"
			Assert.Equal(0, fit.Sections[1].VisibleRows);
			Assert.Equal(10, fit.Sections[1].HiddenRows);
			Assert.Equal(28 + 80 + 28 + 40, fit.Height);
		}

		[Fact]
		public void Fit_MinimumsThatExactlyFitDontScroll()
		{
			var fit = DockStackAllocator.Fit(new[] { ListSection(rows: 10) }, available: 28 + 40);
			Assert.False(fit.NeedsScroll);
			Assert.Equal(0, fit.Sections[0].VisibleRows);
		}

		[Fact]
		public void Fit_RowCapHidesTheRestEvenWithRoom()
		{
			var fit = DockStackAllocator.Fit(new[] { ListSection(rows: 20, cap: 8) }, available: 1000);
			Assert.Equal(8, fit.Sections[0].VisibleRows);
			Assert.Equal(12, fit.Sections[0].HiddenRows);
			Assert.Equal(20 + 8 * 20 + 20, fit.Sections[0].BodyHeight);
		}

		[Fact]
		public void Fit_SpacingIsFixed()
		{
			var fit = DockStackAllocator.Fit(new[]
			{
				new DockSectionSize(28, false, 0),
				new DockSectionSize(28, false, 0),
				new DockSectionSize(28, false, 0),
			}, available: 1000, spacing: 4);
			Assert.Equal(3 * 28 + 2 * 4, fit.Height);
		}

		[Fact]
		public void Fit_NoSectionsIsEmpty()
		{
			var fit = DockStackAllocator.Fit(System.Array.Empty<DockSectionSize>(), available: 500);
			Assert.Empty(fit.Sections);
			Assert.Equal(0, fit.Height);
			Assert.False(fit.NeedsScroll);
		}
	}
}
