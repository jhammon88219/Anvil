using System.Linq;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Guards SpcOutlookService.ReadHatchGroups — what the ForeCast legend marks "In Outlook". It must agree
	/// with outlook.js sigRank, or the legend denies hatching the map is drawing.
	/// </summary>
	public class OutlookHatchGroupTests
	{
		private static string Collection(params string[] labels) =>
			"{\"type\":\"FeatureCollection\",\"features\":[" +
			string.Join(",", System.Array.ConvertAll(labels, l => "{\"properties\":{\"LABEL\":\"" + l + "\"}}")) +
			"]}";

		[Fact]
		public void ReportsOnlyTheGroupsPresent()
		{
			var groups = SpcOutlookService.ReadHatchGroups(Collection("0.05", "0.10", "CIG1", "CIG2"));
			Assert.Equal(new[] { "CIG1", "CIG2" }, groups.OrderBy(g => g));
		}

		[Fact]
		public void LegacySign_CountsAsCig1()
		{
			Assert.Equal(new[] { "CIG1" }, SpcOutlookService.ReadHatchGroups(Collection("0.15", "SIGN")));
		}

		[Fact]
		public void NoHatchedAreas_IsEmpty()
		{
			Assert.Empty(SpcOutlookService.ReadHatchGroups(Collection("MRGL", "SLGT")));
		}

		[Fact]
		public void MalformedOrEmptyFile_IsEmpty()
		{
			Assert.Empty(SpcOutlookService.ReadHatchGroups("not json"));
			Assert.Empty(SpcOutlookService.ReadHatchGroups("{\"type\":\"FeatureCollection\",\"features\":[]}"));
		}
	}
}
