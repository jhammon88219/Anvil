using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Resolves a storm motion by walking an ORDERED list of wind-profile providers and running the first
	/// profile that satisfies Bunkers coverage through <see cref="BunkersStormMotion"/>.
	/// </summary>
	/// <remarks>
	/// Doc 01 §5: an ordered strategy list, not an if/else tree. A provider that returns null, or whose
	/// profile fails coverage, is skipped and the next is tried.
	///
	/// <para>⚠️ The result carries <see cref="StormMotionResult.ProfileSource"/> and it MUST reach the UI. A
	/// Bunkers vector from the NWS's own VAD and one from our Level II retrieval are different claims about
	/// how much to trust the number.</para>
	///
	/// <para>⚠️ When EVERY provider declines, the returned failure is the one from the FIRST provider that
	/// actually produced a profile — that is the informative reason ("no 5.5–6 km wind") rather than the last
	/// provider's generic "no profile". If nothing produced a profile at all, the result is
	/// <see cref="StormMotionFailure.NoProfile"/>.</para>
	/// </remarks>
	// ⚠️⚠️ PARKED 2026-09-03 — storm motion is FINISHED FOR NOW and deliberately left alone to collect
	// real-use logs. Verified on both paths before parking: NVW live (matched the NWS's own VAD to 2°/5 kt
	// on the same volume) and the local VAD via PastCast Moore 2013.
	// ⚠️ A SPEC-FAITHFUL CHANGE MADE DURING THAT SESSION COST ~4× THE RING POINTS and was caught only by
	// running real data against an independent reference — no test, build or review caught it.
	// Read docs/radar/storm-motion.md (esp. §2.4 and §5) BEFORE changing anything here, and verify on both
	// paths before re-parking.
	public sealed class StormMotionService
	{
		private readonly IReadOnlyList<IWindProfileProvider> _providers;

		public StormMotionService(IReadOnlyList<IWindProfileProvider> providers)
			=> _providers = providers ?? Array.Empty<IWindProfileProvider>();

		public async Task<StormMotionResult> ResolveAsync(
			string siteId, DateTime volumeTimeUtc, CancellationToken cancellationToken = default)
		{
			StormMotionResult? firstRealFailure = null;

			foreach (var provider in _providers)
			{
				cancellationToken.ThrowIfCancellationRequested();

				WindProfile? profile;
				try
				{
					profile = await provider.TryGetAsync(siteId, volumeTimeUtc, cancellationToken)
						.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception)
				{
					continue; // a provider that throws is a provider that has no answer
				}

				if (profile is null)
				{
					continue;
				}

				var result = BunkersStormMotion.Compute(profile);
				if (result.HasSolution)
				{
					return result;
				}

				firstRealFailure ??= result;
			}

			return firstRealFailure
				?? StormMotionResult.NoSolution(StormMotionFailure.NoProfile, volumeTimeUtc, null);
		}
	}
}
