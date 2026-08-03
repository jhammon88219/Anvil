namespace Anvil.Models
{
	/// <summary>
	/// One volume in the fixed velocity-dealias validation corpus (see
	/// <c>Assets/RadarCorpus/radar-corpus.json</c> and <see cref="Anvil.Services.RadarCorpusProvider"/>).
	/// A committed (or locally-referenced) <c>.V06</c> replayed through the real decode/dealias path
	/// and scored on its over-unfold ratio, with a baseline the scorer flags regressions against.
	/// </summary>
	/// <param name="Id">Stable identifier used to correlate the JS decode result with this entry
	/// (e.g. <c>KBUF-134625</c>). Must be unique within the manifest.</param>
	/// <param name="File">The volume's filename under <c>Assets/RadarCorpus/</c> (served via the
	/// <c>radarcorpus</c> host). To score a volume without committing it, drop the <c>.V06</c> into that
	/// same folder and list it here by filename — just don't <c>git add</c> it (the local-folder override).</param>
	/// <param name="Name">Human label for the report (site + why it's in the corpus).</param>
	/// <param name="Lat">Antenna latitude — passed to the decoder for gate projection (does not affect
	/// the over-unfold count, which is azimuth-only, but keeps the decode clean).</param>
	/// <param name="Lon">Antenna longitude.</param>
	/// <param name="ExpectedPct">The established baseline over-unfold percentage (hi/total ×100).</param>
	/// <param name="TolerancePct">Percentage-point margin above <paramref name="ExpectedPct"/> before a
	/// run flags the volume as having gotten <c>Worse</c> — the KTLX-3×-regression guard.</param>
	/// <param name="DualPol">Optional per-product decoded-mean baselines for the DUAL-POL decoder guard.
	/// Null ⇒ no dual-pol check for this volume. The direct-read dual-pol moments (CC/ZDR/SW) decode
	/// deterministically from the fixed bytes, so their mean is a stable fingerprint — a scale/offset
	/// regression in the JS decoder shifts it. See docs/radar-validation.md "Dual-pol validation".</param>
	public sealed record RadarCorpusEntry(
		string Id,
		string File,
		string Name,
		double Lat,
		double Lon,
		double ExpectedPct,
		double TolerancePct,
		DualPolBaseline? DualPol = null);

	/// <summary>Expected decoded MEAN (over the deterministic corpus volume) for each direct-read dual-pol
	/// product — the dual-pol decoder guard's baseline. A field is null when that product isn't scored.</summary>
	/// <param name="Cc">Correlation coefficient ρHV (unitless).</param>
	/// <param name="Zdr">Differential reflectivity (dB).</param>
	/// <param name="Sw">Spectrum width (m/s).</param>
	public sealed record DualPolBaseline(double? Cc, double? Zdr, double? Sw);
}
