using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The "?" hints on Settings → Storage's column headers (<c>Primitives/GlossaryHint</c> cards). Plain words only;
	/// ⚠️ <see cref="Size"/> words <see cref="StorageLimits.MediumAt"/> / <see cref="StorageLimits.HighAt"/> — change together.
	/// </summary>
	public static class StorageGlossary
	{
		public static RadarGlossaryCard Size { get; } = new(
			"Size",
			"How much space this kind of data takes now",
			"The number turns yellow once it reaches 60% of its Max size, and red at 85% — the point where Anvil will soon start deleting the oldest files.",
			"",
			"White: under 60% · Yellow: 60–85% · Red: over 85%. Items with no Max size stay white.");

		public static RadarGlossaryCard MaxSize { get; } = new(
			"Max size",
			"The most space this kind of data may take",
			"When it grows past this, Anvil deletes its oldest files first until it fits. Files from the last half hour are never removed this way.",
			"",
			"Checked when Anvil starts and when you change it. Radar volumes and diagnostics are also checked while Anvil runs.");

		public static RadarGlossaryCard KeepFor { get; } = new(
			"Keep for",
			"How long a file is kept, whatever the size",
			"Files older than this are deleted. Data Anvil still uses is rewritten as it updates, so it never gets this old.",
			"",
			"Checked when Anvil starts and when you change it.");
	}
}
