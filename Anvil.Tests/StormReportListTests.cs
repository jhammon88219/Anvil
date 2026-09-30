using System.IO;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The Storm reports LIST: row wording (mirrors storm-reports.js magText), newest-first over the CONVECTIVE
	/// day, counts taken from the same list, and a cache file read back into rows.
	/// </summary>
	public class StormReportListTests
	{
		private static StormReportItem R(string kind, string time, string mag = "UNK", string loc = "X", string st = "KS") =>
			new(kind, 38, -99, time, mag, loc, "Pawnee", st);

		[Fact]
		public void RowWording_MatchesThePopup()
		{
			var t = R("torn", "2049", "2", "3 SSW Ash Valley");
			Assert.Equal("20:49Z", t.TimeText);
			Assert.Equal("3 SSW Ash Valley, KS", t.Place);
			Assert.Equal("EF2", t.MagText);
			Assert.Equal("65 mph", R("wind", "1812", "65").MagText);
			Assert.Equal("1.75 in", R("hail", "0105", "175").MagText);
			Assert.Equal("01:05Z", R("hail", "105").TimeText);
			Assert.Equal(string.Empty, R("torn", "1600").MagText);   // UNK says nothing
		}

		[Fact]
		public void Newest_IsOverTheConvectiveDay_SoEarlyMorningIsNewest()
		{
			var rows = StormReportsViewModel.Newest(new[] { R("torn", "1600"), R("wind", "0105"), R("hail", "2330"), R("wind", "1200") });
			Assert.Equal(new[] { "0105", "2330", "1600", "1200" }, rows.Select(r => r.Time));
		}

		[Fact]
		public void Counts_ComeFromTheList()
		{
			var result = StormReportService.Found(new[] { R("torn", "1600"), R("torn", "2049"), R("wind", "1812"), R("zzz", "1300") });
			Assert.Equal((2, 1, 0), (result.Tornado, result.Wind, result.Hail));
			Assert.Equal(4, result.Reports!.Count);                      // an odd kind lists but counts nowhere, as before
		}

		[Fact]
		public void ReadItems_ReadsBackWhatTheWriterWrites()
		{
			var path = Path.GetTempFileName();
			try
			{
				File.WriteAllText(path, """
					{"type":"FeatureCollection","features":[
					 {"type":"Feature","geometry":{"type":"Point","coordinates":[-80.48,25.05]},
					  "properties":{"kind":"torn","time":"1600","mag":"UNK","loc":"2 SW Rock Harbor","county":"FLC087","st":"FL","com":"…"}},
					 {"type":"Feature","geometry":null,"properties":{"kind":"wind"}}]}
					""");
				var row = Assert.Single(StormReportService.ReadItems(path));
				Assert.Equal(("torn", 25.05, -80.48, "2 SW Rock Harbor, FL"), (row.Kind, row.Lat, row.Lon, row.Place));
			}
			finally
			{
				File.Delete(path);
			}
		}
	}
}
