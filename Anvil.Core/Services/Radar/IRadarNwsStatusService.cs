using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The NWS's own account of each radar: what the RDA reports about itself, and the outage notices (FTMs)
	/// WFOs issue for it. Two independent feeds, two calls — the caller keeps each half's last good answer.
	/// Both THROW on a failed fetch or an unreadable body; neither returns "empty" for a failure.
	/// </summary>
	public interface IRadarNwsStatusService
	{
		/// <summary>Every station's self-reported state, keyed by id (case-insensitive). One request, ~500 KB.</summary>
		Task<IReadOnlyDictionary<string, RadarNwsStation>> GetStationsAsync(CancellationToken ct = default);

		/// <summary>Every FTM from the last 24 h, newest first. One request, ~15 KB.</summary>
		Task<IReadOnlyList<RadarNwsMessage>> GetMessagesAsync(CancellationToken ct = default);
	}
}
