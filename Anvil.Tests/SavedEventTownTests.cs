using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="PlaceSearchViewModel.NamedTown"/>: which town a PastCast saved event's name pins — "Town, ST"
	/// as is, a hyphenated pair's FIRST town, and nothing for a name with no 2-letter state.
	/// </summary>
	public class SavedEventTownTests
	{
		[Theory]
		[InlineData("Moore, OK", "Moore, OK")]
		[InlineData("El Reno-Piedmont, OK", "El Reno, OK")]
		[InlineData("Hackleburg-Phil Campbell, AL", "Hackleburg, AL")]
		[InlineData("  Joplin ,  MO ", "Joplin, MO")]
		public void TownNames_Resolve(string name, string expected) =>
			Assert.Equal(expected, PlaceSearchViewModel.NamedTown(name));

		[Theory]
		[InlineData("Hurricane Katrina")]
		[InlineData("Ohio Valley-Mid-Atlantic derecho")]
		[InlineData("Chase day, spring")]
		[InlineData(", OK")]
		[InlineData("")]
		[InlineData(null)]
		public void NonTownNames_PinNothing(string? name) =>
			Assert.Null(PlaceSearchViewModel.NamedTown(name));
	}
}
