using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>The HRRR's runs and fields (see <see cref="HrrrService"/>).</summary>
	public interface IHrrrService
	{
		/// <summary>The newest run with at least its analysis (f00) published, or null when none in the last 6 h is.</summary>
		Task<HrrrRunStatus?> FindLatestRunAsync(CancellationToken cancellationToken = default);

		/// <summary>Which hours of <paramref name="runUtc"/> have landed (one bucket listing).</summary>
		Task<HrrrRunStatus> GetRunStatusAsync(DateTimeOffset runUtc, CancellationToken cancellationToken = default);

		/// <summary>One field at one lead: a range read of just its message (≈ 200 KB), cached on disk, decoded.</summary>
		/// <exception cref="HrrrNotAvailableException">That hour or field hasn't been published yet.</exception>
		Task<GribField> GetFieldAsync(DateTimeOffset runUtc, HrrrField field, int leadMinutes, CancellationToken cancellationToken = default);
	}

	/// <summary>Where one HRRR run stands: which forecast hours have landed in each file kind.</summary>
	/// <param name="HourlyIn">Hours of the hourly (<c>wrfsfc</c>) file whose index exists, ascending.</param>
	/// <param name="SubHourlyIn">Hours of the sub-hourly (<c>wrfsubh</c>, 15-min) file whose index exists, ascending.</param>
	public sealed record HrrrRunStatus(DateTimeOffset RunUtc, int LastHour, IReadOnlyList<int> HourlyIn, IReadOnlyList<int> SubHourlyIn)
	{
		public bool IsComplete => HourlyIn.Count > 0 && HourlyIn[^1] >= LastHour;
	}

	/// <summary>The hour or field asked for hasn't been published (yet) — normal while a run is still landing.</summary>
	public sealed class HrrrNotAvailableException(string message) : Exception(message);
}
