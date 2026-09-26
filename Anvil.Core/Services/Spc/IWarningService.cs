using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Fetches and caches the active, storm-based NWS Tornado / Severe Thunderstorm WARNING polygons
	/// (the modern forecaster-drawn polygons, not county areas) as local GeoJSON for the map. HTTP
	/// happens here (C# side, avoiding the WebView's CORS limits); this service never touches WebView2.
	/// The view (MainWindow) maps <see cref="CacheDirectory"/> to a WebView virtual host so the page can
	/// fetch the cached file. Sibling of <see cref="ISpcWatchService"/> — watches are the large outlook
	/// areas; warnings are the imminent-threat polygons, so they get their own layer + toggle.
	/// </summary>
	public interface IWarningService
	{
		/// <summary>Absolute path of the on-disk GeoJSON cache folder. The view maps this to a
		/// WebView virtual host; the service itself never does.</summary>
		string CacheDirectory { get; }

		/// <summary>The local virtual-host URL the page loads the cached warning GeoJSON from.</summary>
		string WarningsUrl { get; }

		/// <summary>
		/// Fetches the warning polygons and writes the currently-active ones to the cache. The
		/// last-known-good file is kept on failure; failures are reported via the result rather
		/// than thrown.
		/// </summary>
		Task<WarningFetchResult> RefreshAsync(CancellationToken cancellationToken = default);
	}

	/// <summary>Outcome of a single <see cref="IWarningService.RefreshAsync"/> call.</summary>
	public enum WarningFetchStatus { Updated, FailedCacheKept, FailedNoCache }

	/// <summary>Result of a refresh: status, the number of active warnings written (total + per phenom for
	/// the UI readout), and a message. Per-type counts are meaningful only on <see cref="WarningFetchStatus.Updated"/>.</summary>
	public sealed record WarningFetchResult(WarningFetchStatus Status, int ActiveCount = 0, int TornadoCount = 0, int SevereCount = 0, string? Message = null,
		int FlashFloodCount = 0, WarningThreatCounts? Threats = null);

	/// <summary>How many active warnings carry each ELEVATED damage-threat tag (the IBW tags CAP reports —
	/// see <c>WarningService.ThreatTier</c>). Base-tagged warnings aren't counted here.</summary>
	public sealed record WarningThreatCounts(int TornadoEmergency, int TornadoPds, int SevereDestructive,
		int FlashFloodEmergency, int FlashFloodConsiderable)
	{
		public static readonly WarningThreatCounts None = new(0, 0, 0, 0, 0);

		/// <summary>Counts from alerts' (phenom, tier) — the one tally live and PastCast warnings share.</summary>
		public static WarningThreatCounts From(IEnumerable<(string Phenom, int Tier)> alerts)
		{
			int torE = 0, torP = 0, svD = 0, ffE = 0, ffC = 0;
			foreach (var (phenom, tier) in alerts)
			{
				switch (phenom)
				{
					case "TO": if (tier == 2) { torE++; } else if (tier == 1) { torP++; } break;
					case "SV": if (tier == 2) { svD++; } break;
					case "FF": if (tier == 2) { ffE++; } else if (tier == 1) { ffC++; } break;
				}
			}
			return new WarningThreatCounts(torE, torP, svD, ffE, ffC);
		}

		/// <summary>The card line, worst first — "1 tornado emergency · 2 PDS tornado". Empty when none.</summary>
		public string ToCardLine()
		{
			var parts = new List<string>(5);
			Add(TornadoEmergency, "tornado emergency", "tornado emergencies");
			Add(FlashFloodEmergency, "flash flood emergency", "flash flood emergencies");
			Add(TornadoPds, "PDS tornado", "PDS tornado");
			Add(SevereDestructive, "destructive storm", "destructive storms");
			Add(FlashFloodConsiderable, "considerable flash flood", "considerable flash flood");
			return string.Join(" · ", parts);

			void Add(int n, string one, string many)
			{
				if (n > 0) { parts.Add($"{n} {(n == 1 ? one : many)}"); }
			}
		}
	}
}
