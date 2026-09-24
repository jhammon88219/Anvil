using System;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Fetches, caches, and exposes NWS Damage Assessment Toolkit (DAT) tornado surveys — the damage
	/// polygons, track centerlines and survey points the DAT viewer draws — as one local GeoJSON per replay
	/// window. HTTP happens here (C# side, no WebView CORS); this service never touches WebView2. The view
	/// (MainWindow) maps <see cref="CacheDirectory"/> to a WebView virtual host so the page can fetch it.
	///
	/// Keyed to the LOADED replay WINDOW, not a day: a feature is in the window file when its time span
	/// overlaps the window. Tornado features only — the DAT's wind and tropical entries are dropped. Tracks
	/// also come from NCEI Storm Events (1950 → ~3 months ago), which times the DAT features it matches.
	/// </summary>
	public interface IDamageSurveyService
	{
		/// <summary>Absolute path of the on-disk cache folder. The view maps this to a WebView virtual host;
		/// the service itself never does.</summary>
		string CacheDirectory { get; }

		/// <summary>The <c>damagesurveys</c>-host URL the page loads a window's features from.</summary>
		string LocalUrl(DateTimeOffset startUtc, DateTimeOffset endUtc);

		/// <summary>
		/// Ensures the UTC days the window touches are cached (raw DAT layers, one file per day per layer),
		/// then writes the window file: tornado features overlapping [start, end], each damage polygon
		/// tagged with the track it lies on (the DAT publishes no link between the two). A day older than the
		/// survey-revision horizon is fetched once; a recent one is re-fetched when its cache is over an hour
		/// old, since surveys land and get revised in the days after an event. A failed fetch falls back to
		/// the last-known-good day file.
		/// </summary>
		Task<DamageSurveyResult> EnsureWindowAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
	}
}
