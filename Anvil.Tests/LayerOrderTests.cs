using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="LayerOrder.Normalize"/>: the gate between a saved layer order (hand-editable JSON) and the
	/// page, which only knows the groups in layers.js GROUPS.
	/// </summary>
	public class LayerOrderTests
	{
		[Fact]
		public void KeepsOrderOfKnownIds()
		{
			var ids = LayerOrder.Normalize(new[] { "radar", "reports", "warnings" });
			Assert.Equal(new[] { "radar", "reports", "warnings" }, ids);
		}

		[Fact]
		public void DropsUnknownAndRepeatedIds()
		{
			var ids = LayerOrder.Normalize(new[] { "reports", "lightning", "radar", "reports", "Radar" });
			Assert.Equal(new[] { "reports", "radar" }, ids);
		}

		[Fact]
		public void NullIsEmpty() => Assert.Empty(LayerOrder.Normalize(null));

		// LayerOrder.Complete — the ghost rows' fill-in, mirroring layers.js effectiveOrder().

		[Fact]
		public void Complete_PutsAMissingLayerBeneathItsDefaultNeighbourAbove()
		{
			// A NowCast save from before ghost rows: no outlook (and never damage, a PastCast layer).
			// (And no tropical either — it arrived 2026-10-07 and lands beneath watches, its default neighbour.)
			var ids = LayerOrder.Complete(new[] { "cells", "warnings", "reports", "watches", "mds", "radar" });
			Assert.Equal(new[] { "cells", "warnings", "reports", "damage", "watches", "tropical", "mds", "outlook", "radar" }, ids);
		}

		[Fact]
		public void Complete_KeepsWhereTheSaveAlreadyPutsIt()
		{
			var saved = new[] { "outlook", "cells", "reports", "damage", "warnings", "watches", "tropical", "mds", "radar" };
			Assert.Equal(saved, LayerOrder.Complete(saved));
		}

		[Fact]
		public void Complete_MissingTopLayerGoesOnTop()
		{
			var ids = LayerOrder.Complete(new[] { "radar", "reports" });
			Assert.Equal("cells", ids[0]);
			Assert.Equal(LayerOrder.Default.Count, ids.Count);
		}

		[Fact]
		public void Complete_EmptyIsTheDefault() => Assert.Equal(LayerOrder.Default, LayerOrder.Complete(null));
	}
}
