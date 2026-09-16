using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The one scan-mode cut (<see cref="RadarViewModel.ScanStrategyText"/>) shared by the bar and the Radar
	/// Atlas. The regression: the Atlas showed the whole string, so "SAILS/MRLE ×1 · 0.5°×2" read as the bar's
	/// "×1" disagreeing with a "×2".
	/// </summary>
	public class ScanStrategyTextTests
	{
		[Theory]
		[InlineData("VCP 212 · precip · SAILS/MRLE ×1 · 0.5°×2", "VCP 212 · precip · SAILS/MRLE ×1")]
		[InlineData("VCP 35 · clear-air · 0.5°×1", "VCP 35 · clear-air")]
		[InlineData("VCP ? · 0.5°×3", "VCP ?")]
		[InlineData("VCP 212 · precip", "VCP 212 · precip")] // archive path: no sweep token
		[InlineData("—", "—")]
		[InlineData("loading…", "loading…")]
		[InlineData("", "")]
		[InlineData(null, "")]
		public void DropsTheSweepToken(string? mode, string expected) =>
			Assert.Equal(expected, RadarViewModel.ScanStrategyText(mode));
	}
}
