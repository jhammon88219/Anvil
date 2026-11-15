using System;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// What Anvil keeps on disk, by <see cref="StorageKind"/> — Settings → Storage's sizes, clears, folders and the
	/// weather-data + log clean-up. Radar VOLUMES stay with <see cref="ILevel2RadarService"/> (its own size readout,
	/// clear and sweep — the sealed engine); this service only measures/clears the other four and opens folders.
	/// </summary>
	public interface IStorageService
	{
		/// <summary>The folder Settings → Storage opens for <paramref name="kind"/> (weather data = Anvil's root,
		/// since it spans one folder per feed).</summary>
		string FolderOf(StorageKind kind);

		/// <summary>Bytes on disk for a kind other than <see cref="StorageKind.RadarVolumes"/> (off-thread).</summary>
		Task<long> MeasureAsync(StorageKind kind, CancellationToken cancellationToken = default);

		/// <summary>Delete a kind's files, best effort (a file in use stays). Not radar volumes; for usage history
		/// this removes the log files only — the caller clears the live site-usage record itself.</summary>
		Task ClearAsync(StorageKind kind, CancellationToken cancellationToken = default);

		/// <summary>Apply a kind's limits: delete files older than <paramref name="maxAge"/>, then oldest-first until
		/// under <paramref name="maxBytes"/> (files written in the last 30 min are never size-trimmed). Weather data
		/// and logs only.</summary>
		Task SweepAsync(StorageKind kind, long maxBytes, TimeSpan maxAge, CancellationToken cancellationToken = default);
	}
}
