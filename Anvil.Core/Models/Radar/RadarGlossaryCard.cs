namespace Anvil.Models
{
	/// <summary>
	/// One "what am I looking at" explanation, as the Radar Atlas's hint popovers show it. Five parts in the
	/// order a newcomer needs them: the plain <see cref="Term"/>, the <see cref="Technical"/> name an expert
	/// would use, a jargon-free <see cref="Definition"/>, this site's own value read back in words
	/// (<see cref="Now"/>), and the <see cref="Context"/> that gives the value a scale.
	/// </summary>
	/// <remarks>
	/// ⚠️ <see cref="Now"/> is the only part computed from live data — every other part is fixed text from
	/// <c>RadarGlossary</c>. A card with an empty Now (a static fact like coordinates) is normal; the popover
	/// simply omits that block. Built in Core so the words live with the rule they describe, not in XAML.
	/// </remarks>
	public sealed record RadarGlossaryCard(
		string Term,
		string Technical,
		string Definition,
		string Now,
		string Context);
}
