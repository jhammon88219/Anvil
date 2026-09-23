using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// The per-site usage record (<see cref="SiteUsage"/>) behind the Radar Atlas's "Your use" strip, persisted
	/// to <c>%LocalAppData%\Anvil\Usage\site-usage.json</c>. A plain store: it counts what it is told —
	/// deciding WHEN a load or a stretch of site load time happened is <c>SiteUsageTracker</c>'s job.
	/// </summary>
	/// <remarks>
	/// ⚠️ NOT in <c>AppSettings</c>: this is data that grows with every site you touch, not a preference, and
	/// "Reset settings" must not wipe it (nor Clear usage wipe your settings).
	/// ⚠️ Writes are rare (a load, a selection change, a clear, shutdown) so each one saves immediately, via a
	/// unique temp file + move — same reasoning as <c>SavedEventLibrary</c>: a crash mid-write must leave the
	/// previous file intact. UI-thread only, like its one caller.
	/// </remarks>
	public sealed class SiteUsageStore
	{
		private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

		private readonly ILogger<SiteUsageStore> _logger;
		private readonly string _filePath;
		private readonly Dictionary<string, SiteUsage> _sites;

		/// <param name="logger">Injected by the container (Serilog-backed).</param>
		/// <param name="directory">TESTS ONLY — where the file lives. Null (the DI default) uses
		/// <c>%LocalAppData%\Anvil\Usage</c>.</param>
		public SiteUsageStore(ILogger<SiteUsageStore> logger, string? directory = null)
		{
			_logger = logger;
			var dir = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Usage");
			Directory.CreateDirectory(dir);
			_filePath = Path.Combine(dir, "site-usage.json");
			_sites = Load();
			// What this launch found. Kept permanently: a launch once found NO file after a session that had
			// saved, and nothing logged why. This line plus the "saved" lines show whether a session's writes
			// reach the next launch — a handful of lines per session.
			_logger.LogInformation("SiteUsageStore loaded {Count} site(s) from {Path} (exists={Exists})",
				_sites.Count, _filePath, File.Exists(_filePath));
		}

		/// <summary>Raised after any change, with the site ICAO — or null when EVERY site changed (clear all).</summary>
		public event EventHandler<string?>? Changed;

		/// <summary>This site's record, or null if it has never been loaded.</summary>
		public SiteUsage? Get(string siteId) => _sites.TryGetValue(siteId, out var u) ? u : null;

		/// <summary>Every used site's record, by ICAO.</summary>
		public IReadOnlyDictionary<string, SiteUsage> All => _sites;

		/// <summary>Count one load (live or replay) at <paramref name="now"/>.</summary>
		public void RecordLoad(string siteId, bool replay, DateTimeOffset now)
		{
			var u = GetOrAdd(siteId);
			if (replay) u.ReplayLoads++;
			else u.LiveLoads++;
			u.FirstUsedUtc ??= now;
			u.LastUsedUtc = Latest(u.LastUsedUtc, now);
			Commit(siteId);
		}

		/// <summary>Add a stretch of site load time, in the bucket of the mode it ran in, that ended at
		/// <paramref name="endedAt"/>. Zero/negative is ignored.</summary>
		public void AddTimeLoaded(string siteId, bool replay, TimeSpan span, DateTimeOffset endedAt)
		{
			if (span <= TimeSpan.Zero) return;
			var u = GetOrAdd(siteId);
			if (replay) u.ReplaySeconds += span.TotalSeconds;
			else u.LiveSeconds += span.TotalSeconds;
			u.FirstUsedUtc ??= endedAt - span;
			u.LastUsedUtc = Latest(u.LastUsedUtc, endedAt);
			Commit(siteId);
		}

		/// <summary>Forget one site's record (a no-op if there is none).</summary>
		public void Clear(string siteId)
		{
			if (_sites.Remove(siteId)) Commit(siteId);
		}

		/// <summary>Forget every site's record.</summary>
		public void ClearAll()
		{
			if (_sites.Count == 0) return;
			// Logged so a "my stats vanished" report can tell a user clear from a lost file.
			_logger.LogInformation("SiteUsageStore ClearAll ({Count} site(s))", _sites.Count);
			_sites.Clear();
			Commit(null);
		}

		private SiteUsage GetOrAdd(string siteId)
		{
			if (!_sites.TryGetValue(siteId, out var u))
			{
				u = new SiteUsage();
				_sites[siteId] = u;
			}
			return u;
		}

		private static DateTimeOffset Latest(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

		private void Commit(string? siteId)
		{
			Save();
			Changed?.Invoke(this, siteId);
		}

		// ── persistence ──

		private Dictionary<string, SiteUsage> Load()
		{
			var empty = new Dictionary<string, SiteUsage>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(_filePath)) return empty;
			try
			{
				var loaded = JsonSerializer.Deserialize<Dictionary<string, SiteUsage>>(File.ReadAllText(_filePath));
				if (loaded is null) return empty;

				// The first build kept ONE unsplit total. Fold it into NowCast — PastCast couldn't be timed apart
				// then — and drop it, so the next save writes only the split form.
				foreach (var u in loaded.Values)
				{
					if (u.SecondsLoaded is { } legacy)
					{
						u.LiveSeconds += legacy;
						u.SecondsLoaded = null;
					}
				}
				return new Dictionary<string, SiteUsage>(loaded, StringComparer.OrdinalIgnoreCase);
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
			{
				// Usage stats are a nicety — a bad file costs the history, never a launch.
				_logger.LogWarning(ex, "Site usage file unreadable; starting empty.");
				return empty;
			}
		}

		private void Save()
		{
			try
			{
				var json = JsonSerializer.Serialize(
					_sites.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(kv => kv.Key, kv => kv.Value),
					SerializerOptions);
				var temp = $"{_filePath}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
				File.WriteAllText(temp, json);
				File.Move(temp, _filePath, overwrite: true);
				// Read the file straight back — if the move didn't land where the next launch will look, this says
				// so now instead of as missing stats later. Saves are rare (a load, a 5-min checkpoint, a clear,
				// close), so the check and the line cost nothing that matters.
				var onDisk = File.Exists(_filePath) ? new FileInfo(_filePath).Length : -1;
				_logger.LogInformation("SiteUsageStore saved {Count} site(s) ({Bytes} bytes written, {OnDisk} on disk, temp left={TempLeft})",
					_sites.Count, json.Length, onDisk, File.Exists(temp));
			}
			catch (Exception ex)
			{
				// Catch-all ON PURPOSE: Save runs inside the SelectedRadarOption setter and the window's Closed
				// handler, where anything that escaped would vanish without a trace. Usage stats are never worth
				// breaking either — log and move on.
				_logger.LogWarning(ex, "Failed to save site usage.");
			}
		}
	}
}
