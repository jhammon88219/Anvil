using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Finds US cities/towns by name for the map-tools tier's search box. OFFLINE FIRST: a bundled gazetteer
	/// answers every keystroke; the online geocoder is a fallback for a submitted query the gazetteer can't
	/// place.
	/// </summary>
	public interface IPlaceSearchService
	{
		/// <summary>Whether the bundled gazetteer loaded. False = the catalog was never generated (or failed to
		/// parse) and every search goes online.</summary>
		bool HasOfflineCatalog { get; }

		/// <summary>Up to <paramref name="max"/> gazetteer matches for a partially-typed query, best first.
		/// Accepts "Moore", "Moore, OK", "moore oklahoma". Synchronous and cheap — call it per keystroke.</summary>
		IReadOnlyList<PlaceResult> Suggest(string? query, int max = 8);

		/// <summary>Asks the online geocoder (Nominatim / OpenStreetMap), US settlements only. Never throws for a
		/// network failure — that is an empty list, logged.</summary>
		/// <remarks>⚠️ SUBMIT ONLY, never per keystroke: Nominatim's usage policy forbids autocomplete and caps
		/// clients at one request a second. The service enforces the rate; the caller must keep it on Enter.</remarks>
		Task<IReadOnlyList<PlaceResult>> SearchOnlineAsync(string? query, CancellationToken ct = default);
	}
}
