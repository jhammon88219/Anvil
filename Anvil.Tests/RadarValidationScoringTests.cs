using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The scoring core of the velocity-dealias regression harness (<see cref="RadarValidationReport.Classify"/>).
	/// This is the whole point of the harness — deciding whether a dealias change made a fixed volume WORSE
	/// than its recorded baseline (the reverted VAD gap-fill that made KTLX 3× worse is the failure mode to
	/// guard against). Pure + deterministic, so it's tested without a WebView.
	///
	/// ⚠️ A regression test that can't fail is worthless: mutate the <c>&gt;</c> in Classify to <c>&gt;=</c>
	/// and <see cref="BaselinePlusToleranceExactly_IsPass"/> goes red — that's how the boundary is pinned.
	/// </summary>
	public class RadarValidationScoringTests
	{
		private static RadarCorpusEntry Entry(double expectedPct, double tolerancePct) =>
			new("KTEST-1", "KTEST.V06", "Test site", 0, 0, expectedPct, tolerancePct);

		[Fact]
		public void AtBaseline_IsPass()
		{
			// KBUF's established baseline reproduced exactly — the harness's happy path.
			Assert.Equal(ValidationStatus.Pass, RadarValidationReport.Classify(Entry(31.3, 2.0), null, 31.3));
		}

		[Fact]
		public void BelowBaseline_IsPass()
		{
			// Dealias improved (fewer over-unfolds) — never a regression.
			Assert.Equal(ValidationStatus.Pass, RadarValidationReport.Classify(Entry(31.3, 2.0), null, 1.0));
		}

		[Fact]
		public void BaselinePlusToleranceExactly_IsPass()
		{
			// The boundary: actual == baseline + tolerance is still within budget (strict >). Mutating the
			// comparison to >= flips this to Worse — the guard that proves the test bites.
			Assert.Equal(ValidationStatus.Pass, RadarValidationReport.Classify(Entry(4.0, 2.0), null, 6.0));
		}

		[Fact]
		public void JustOverTolerance_IsWorse()
		{
			Assert.Equal(ValidationStatus.Worse, RadarValidationReport.Classify(Entry(4.0, 2.0), null, 6.1));
		}

		[Fact]
		public void KtlxThreeXRegression_IsWorse()
		{
			// The documented failure mode: a change triples the over-unfold (5% baseline -> 15%).
			Assert.Equal(ValidationStatus.Worse, RadarValidationReport.Classify(Entry(5.0, 2.0), null, 15.0));
		}

		[Fact]
		public void NoBaseline_WhenEntryMissing()
		{
			// A decoded volume with no manifest entry can't be scored for regression.
			Assert.Equal(ValidationStatus.NoBaseline, RadarValidationReport.Classify(null, null, 12.0));
		}

		[Fact]
		public void DecodeError_IsErrorEvenWhenActualLooksFine()
		{
			// A fetch/decode failure or a velocity-less volume can't be trusted, whatever ratio surfaced.
			Assert.Equal(ValidationStatus.Error, RadarValidationReport.Classify(Entry(4.0, 2.0), "no velocity", 0.0));
		}

		// ── Dual-pol decoder guard (RadarValidationReport.DualPolDrift) ──
		// A committed volume decodes CC/ZDR/SW deterministically, so their MEAN is a fixed fingerprint; a
		// decoder scale/offset regression shifts it. Baseline KBUF-140342 = cc 0.72 / zdr 0.67 / sw 4.08.

		private static readonly DualPolBaseline Kbuf = new(0.72, 0.67, 4.08);

		[Fact]
		public void DualPol_WithinTolerance_NoDrift()
		{
			// Deterministic reproduction (last-ULP flutter absorbed by the 0.15 margin) is not a regression.
			Assert.Null(RadarValidationReport.DualPolDrift(Kbuf, 0.72, 0.67, 4.08, 0.15));
			Assert.Null(RadarValidationReport.DualPolDrift(Kbuf, 0.73, 0.60, 4.20, 0.15));
		}

		[Fact]
		public void DualPol_ZdrScaleRegression_IsFlagged()
		{
			// The failure mode: a ZDR decode scale bug shifts its mean well beyond tolerance (0.67 -> 1.4).
			var detail = RadarValidationReport.DualPolDrift(Kbuf, 0.72, 1.40, 4.08, 0.15);
			Assert.NotNull(detail);
			Assert.Contains("ZDR", detail);
		}

		[Fact]
		public void DualPol_NoBaseline_NoDrift()
		{
			// A volume without a dualPol baseline in the manifest isn't dual-pol-checked.
			Assert.Null(RadarValidationReport.DualPolDrift(null, 0.72, 99.0, 4.08, 0.15));
		}

		[Fact]
		public void DualPol_MissingActualMean_NotFlagged()
		{
			// A product the decoder didn't report (null actual, e.g. legacy single-pol) can't drift.
			Assert.Null(RadarValidationReport.DualPolDrift(Kbuf, null, null, null, 0.15));
		}

		[Fact]
		public void DualPol_BoundaryIsStrict()
		{
			// Exactly at the tolerance is NOT flagged (strict >). Uses exactly-representable values (¼) so the
			// boundary is exact in float; mutating > to >= in DualPolDrift flips the first assert to a drift —
			// the guard that proves the test bites.
			var b = new DualPolBaseline(1.0, null, null);
			Assert.Null(RadarValidationReport.DualPolDrift(b, 1.25, null, null, 0.25));    // diff == tol → pass
			Assert.NotNull(RadarValidationReport.DualPolDrift(b, 1.75, null, null, 0.25)); // diff  > tol → drift
		}
	}
}
