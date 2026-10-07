using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The ForeCast header's square + readout: the HIGHEST solid category an outlook actually contains
	/// (<see cref="SpcOutlookService.ReadHighest"/>), and how it's worded (<see cref="OutlookViewModel.PeakLabel"/>).
	/// </summary>
	public class OutlookPeakTests
	{
		private static string Features(params string[] props) =>
			"{\"type\":\"FeatureCollection\",\"features\":[" +
			string.Join(",", System.Array.ConvertAll(props, p => "{\"type\":\"Feature\",\"properties\":{" + p + "},\"geometry\":null}")) +
			"]}";

		[Fact]
		public void Highest_IsTheMostSevereCategoryPresent_InTheFeedsColour()
		{
			var json = Features(
				"\"LABEL\":\"MRGL\",\"fill\":\"#66A366\"",
				"\"LABEL\":\"MDT\",\"fill\":\"#E06666\"",
				"\"LABEL\":\"SLGT\",\"fill\":\"#FFE066\"");

			var peak = SpcOutlookService.ReadHighest(json, SpcOutlookType.Categorical);

			Assert.NotNull(peak);
			Assert.Equal("MDT", peak!.Level.Code);
			Assert.Equal("#E06666", peak.Fill);
		}

		[Fact]
		public void Hatching_NeverCounts_AndTheCatalogColoursAFeatureWithoutAFill()
		{
			var json = Features("\"LABEL\":\"0.05\"", "\"LABEL\":\"CIG2\"");

			var peak = SpcOutlookService.ReadHighest(json, SpcOutlookType.Tornado);

			Assert.NotNull(peak);
			Assert.Equal(SpcLevelKind.Solid, peak!.Level.Kind);
			Assert.Equal(peak.Level.Fill, peak.Fill);
		}

		[Fact]
		public void NoAreas_IsNull()
		{
			Assert.Null(SpcOutlookService.ReadHighest(Features("\"DN\":0,\"LABEL\":\"Predictability Too Low\""), SpcOutlookType.Categorical));
			Assert.Null(SpcOutlookService.ReadHighest("{\"features\":[]}", SpcOutlookType.Categorical));
			Assert.Null(SpcOutlookService.ReadHighest("not json", SpcOutlookType.Categorical));
		}

		[Fact]
		public void Wording_Categorical_Probability_AndOther()
		{
			Assert.Equal("MDT 4/5", OutlookViewModel.PeakLabel(SpcRiskCatalog.Level(SpcOutlookType.Categorical, "MDT")!));
			Assert.Equal("TSTM", OutlookViewModel.PeakLabel(SpcRiskCatalog.Level(SpcOutlookType.Categorical, "TSTM")!));
			Assert.Equal("15%", OutlookViewModel.PeakLabel(SpcRiskCatalog.Level(SpcOutlookType.Tornado, "0.15")!));
		}
	}
}
