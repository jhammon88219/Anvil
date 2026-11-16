using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Tests that change <see cref="Anvil.Models.UnitFormat.Current"/> (global, through a view model's unit setters) run in
	/// this collection, ALONE — never beside a test that formats a date or time with the current units (e.g. an expected
	/// "May 24, 2011" would read "2011-05-24" mid-run). Each such test ends on the defaults again.
	/// </summary>
	[CollectionDefinition(Name, DisableParallelization = true)]
	public sealed class UnitFormatCollection
	{
		public const string Name = "UnitFormat (global units)";
	}
}
