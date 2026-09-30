using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// USPS state / territory code → full name, for readouts that group NWS alerts by state (the NowCast tiles'
	/// state lines). CAP's <c>areaDesc</c> names counties as "Cleveland, OK", so the code is what we have.
	/// </summary>
	public static class UsStates
	{
		private static readonly Dictionary<string, string> Names = new(System.StringComparer.OrdinalIgnoreCase)
		{
			["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California",
			["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia",
			["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois",
			["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana",
			["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota",
			["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada",
			["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York",
			["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma", ["OR"] = "Oregon",
			["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota",
			["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia",
			["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming",
			["PR"] = "Puerto Rico", ["VI"] = "U.S. Virgin Islands", ["GU"] = "Guam", ["AS"] = "American Samoa",
			["MP"] = "Northern Mariana Islands",
		};

		/// <summary>"OK" → "Oklahoma". An unknown code comes back as given; empty stays empty.</summary>
		public static string NameOf(string code) =>
			Names.TryGetValue(code.Trim(), out var name) ? name : code.Trim();
	}
}
