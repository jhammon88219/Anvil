using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="DistanceUnits"/>: the token gate, the picker's index mapping, and the formatting rule.
	/// </summary>
	/// <remarks>
	/// ⚠️ WHY FORMATTING IS GUARDED AT ALL: the map page formats the SAME distances for the range ruler's
	/// chip and tick labels (radar-ruler.js, its own UNIT_METERS table and "one decimal below 10" rule),
	/// because a host round-trip per drag frame is not affordable. These tests are the C# half of that
	/// mirror — if you change a divisor or the decimal rule here, change it there in the same edit, or one
	/// screen starts disagreeing with another.
	/// </remarks>
	public class DistanceUnitsTests
	{
		[Theory]
		[InlineData("km", "km")]
		[InlineData("mi", "mi")]
		[InlineData("nm", "nm")]
		[InlineData("", "km")]
		[InlineData(null, "km")]
		[InlineData("MI", "km")]      // case-sensitive by design: the token is written by us, not typed
		[InlineData("furlongs", "km")]
		public void Normalize_FallsBackToKilometers(string? token, string expected) =>
			Assert.Equal(expected, DistanceUnits.Normalize(token));

		[Fact]
		public void IndexAndTokenRoundTrip()
		{
			for (var i = 0; i < DistanceUnits.All.Count; i++)
			{
				Assert.Equal(i, DistanceUnits.IndexOf(DistanceUnits.FromIndex(i)));
			}

			// Out of range reads as kilometres rather than throwing — the index can arrive from a picker
			// whose item list this build no longer has.
			Assert.Equal(DistanceUnits.Kilometers, DistanceUnits.FromIndex(-1));
			Assert.Equal(DistanceUnits.Kilometers, DistanceUnits.FromIndex(99));
		}

		[Fact]
		public void LabelsAreParallelToTokens() =>
			Assert.Equal(DistanceUnits.All.Count, DistanceUnits.Labels.Count);

		[Theory]
		[InlineData(1000, "km", 1.0)]
		[InlineData(1609.344, "mi", 1.0)]
		[InlineData(1852, "nm", 1.0)]
		[InlineData(230000, "km", 230.0)]
		public void FromMeters_ConvertsExactly(double meters, string unit, double expected) =>
			Assert.Equal(expected, DistanceUnits.FromMeters(meters, unit), 6);

		[Theory]
		// At or above 10 the value is whole; below it keeps one decimal, so a near gate still reads.
		[InlineData(151_000, "km", "151 km")]
		[InlineData(9_400, "km", "9.4 km")]
		[InlineData(10_000, "km", "10 km")]
		[InlineData(230_000, "nm", "124 nm")]
		[InlineData(80_467, "mi", "50 mi")]
		[InlineData(0, "km", "0.0 km")]
		public void Format_MirrorsThePageRule(double meters, string unit, string expected) =>
			Assert.Equal(expected, DistanceUnits.Format(meters, unit));

		/// <summary>A radar's full 230 km reach, spoken three ways — the ruler's own headline number.</summary>
		[Fact]
		public void Format_FullRangeInEveryUnit()
		{
			Assert.Equal("230 km", DistanceUnits.Format(230_000, DistanceUnits.Kilometers));
			Assert.Equal("143 mi", DistanceUnits.Format(230_000, DistanceUnits.Miles));
			Assert.Equal("124 nm", DistanceUnits.Format(230_000, DistanceUnits.NauticalMiles));
		}
	}
}
