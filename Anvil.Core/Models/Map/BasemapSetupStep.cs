namespace Anvil.Models
{
	/// <summary>
	/// The screen the map setup window is on (<c>Controls/Basemaps/BasemapSetupWindow</c>). The ordinal is the
	/// window's step index and the Dev tab's preview picker index — append only.
	/// </summary>
	public enum BasemapSetupStep
	{
		/// <summary>Online map (a free key) or offline map (a download) — the two cards.</summary>
		Choose,
		/// <summary>"I already have a map file": pick it, see the checks, use it.</summary>
		ExistingFile,
		/// <summary>An offline map downloading. Anvil stays closed until it finishes.</summary>
		Downloading,
		/// <summary>The map is in place; Open Anvil lights up (never opens by itself — the user's call).</summary>
		Ready,
		/// <summary>The download stopped; what arrived is kept and Retry resumes.</summary>
		Failed,
	}
}
