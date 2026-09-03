using System;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// One source of a vertical wind profile for the Bunkers storm-motion calculation.
	/// </summary>
	/// <remarks>
	/// ⚠️ CONTRACT: <see cref="TryGetAsync"/> returns null for "no data" and NEVER throws for it. A provider
	/// that cannot answer is an ordinary outcome — the chain moves on. Only genuinely exceptional conditions
	/// (cancellation) may propagate.
	///
	/// <para>The wind profile is an ENVIRONMENTAL field. It does not have to come from the same pipe as the
	/// reflectivity being rendered, which is why a Level II app can legitimately take its hodograph from a
	/// Level III product or a model sounding (doc 01 §5).</para>
	/// </remarks>
	public interface IWindProfileProvider
	{
		/// <summary>Short name carried onto the result and shown in the UI. A Bunkers vector from the NWS's
		/// own VAD and one from our Level II retrieval are not the same claim, so the user must see which.</summary>
		string Name { get; }

		/// <summary>The profile nearest <paramref name="volumeTimeUtc"/>, or null when this source has none.</summary>
		Task<WindProfile?> TryGetAsync(string siteId, DateTime volumeTimeUtc, CancellationToken cancellationToken = default);
	}
}
