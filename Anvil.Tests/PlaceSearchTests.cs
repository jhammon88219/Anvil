using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="PlaceSearchService"/>: word-anchored ranking, the state suffix forms, normalization
	/// (diacritics, st/saint), the missing-catalog degrade, and the Nominatim parse. Catalog is inline.
	/// </summary>
	public class PlaceSearchTests
	{
		private const string Catalog = """
			{ "places": [
			  ["Oklahoma City","OK",35.4676,-97.5164,681054],
			  ["Kansas City","MO",39.0997,-94.5786,508090],
			  ["St. Louis","MO",38.6273,-90.1979,293310],
			  ["Springfield","MO",37.2153,-93.2982,169176],
			  ["Kansas City","KS",39.1142,-94.6275,156607],
			  ["Springfield","IL",39.8017,-89.6437,114394],
			  ["Mooresville","NC",35.5849,-80.8101,90000],
			  ["Moore","OK",35.3395,-97.4867,62793],
			  ["Cañon City","CO",38.441,-105.2424,16000],
			  ["Rolling Fork","MS",32.9068,-90.8782,1883],
			  ["Moore Haven","FL",26.8331,-81.0931,1700],
			  ["Sterling","IL",41.7886,-89.6962,14500]
			] }
			""";

		private static PlaceSearchService Service(string? catalog = Catalog) => new(() => catalog);

		private static string[] Names(PlaceSearchService s, string q) => s.Suggest(q).Select(p => p.Display).ToArray();

		[Fact]
		public void ExactBeatsPrefix_ThenPopulation()
		{
			// Mooresville is bigger than Moore, but Moore is the exact name.
			Assert.Equal(new[] { "Moore, OK", "Mooresville, NC", "Moore Haven, FL" }, Names(Service(), "moore"));
		}

		[Fact]
		public void MatchesWordStarts_NotArbitrarySubstrings()
		{
			Assert.Equal(new[] { "Rolling Fork, MS" }, Names(Service(), "fork"));
			Assert.Empty(Names(Service(), "olling"));
		}

		[Theory]
		[InlineData("springfield, mo", "Springfield, MO")]
		[InlineData("Springfield IL", "Springfield, IL")]
		[InlineData("springfield missouri", "Springfield, MO")]
		[InlineData("moore, okla", "Moore, OK")]
		public void StateSuffix_Filters(string query, string expected)
		{
			Assert.Equal(new[] { expected }, Names(Service(), query));
		}

		[Fact]
		public void CommaStatePrefix_NarrowsWhileTyping()
		{
			// "o" is OH/OK/OR — Moore OK stays, Mooresville NC and Moore Haven FL go.
			Assert.Equal(new[] { "Moore, OK" }, Names(Service(), "moore, o"));
		}

		[Fact]
		public void OneWordStateName_SearchesNames()
		{
			Assert.Equal(new[] { "Kansas City, MO", "Kansas City, KS" }, Names(Service(), "kansas"));
		}

		[Fact]
		public void Diacritics_AndSaintAlias()
		{
			Assert.Equal(new[] { "Cañon City, CO" }, Names(Service(), "canon"));
			Assert.Equal(new[] { "St. Louis, MO" }, Names(Service(), "saint louis"));
			Assert.Equal(new[] { "St. Louis, MO" }, Names(Service(), "st louis"));
		}

		[Fact]
		public void TrailingAbbreviationBeingTyped_IsNotAliased()
		{
			// "st" is still being typed: it must keep matching Sterling rather than turning into "saint".
			Assert.Contains("Sterling, IL", Names(Service(), "st"));
		}

		[Fact]
		public void EmptyQuery_AndMissingCatalog_ReturnNothing()
		{
			Assert.Empty(Names(Service(), "  "));
			var none = Service(null);
			Assert.False(none.HasOfflineCatalog);
			Assert.Empty(none.Suggest("moore"));
		}

		[Fact]
		public void Nominatim_ParsesTownAndState()
		{
			const string json = """
				[
				  { "lat": "35.3395", "lon": "-97.4867", "name": "Moore",
				    "address": { "city": "Moore", "state": "Oklahoma", "ISO3166-2-lvl4": "US-OK" } },
				  { "lat": "44.1", "lon": "-70.2", "name": "Tiny",
				    "address": { "hamlet": "Tiny", "state": "Maine" } },
				  { "lon": "-70.2", "name": "No latitude", "address": { "town": "Nowhere" } }
				]
				""";
			var results = PlaceSearchService.ParseNominatim(json);
			Assert.Equal(new[] { "Moore, OK", "Tiny, ME" }, results.Select(p => p.Display).ToArray());
			Assert.All(results, p => Assert.Equal(PlaceSource.Online, p.Source));
			Assert.Equal(-97.4867, results[0].Longitude, 4);
		}
	}
}
