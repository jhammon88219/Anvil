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
	}
}
