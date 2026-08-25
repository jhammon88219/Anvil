using System;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// Where a <see cref="TabStrip"/> draws its tabs: a rail across the TOP of its host, or a rail down the
	/// LEFT side. The two are the same tabs with the same item template — only the stack direction and which
	/// edge carries the selection indicator change.
	/// </summary>
	/// <remarks>
	/// This is a VIEW concept and deliberately lives with the control rather than in Anvil.Core. The
	/// persisted setting behind it (<c>AppSettings.SettingsTabPlacement</c>, surfaced as
	/// <c>MapViewModel.SettingsTabPlacement</c>) is a plain STRING for exactly that reason: Core stores the
	/// user's choice without modelling tab strips. <see cref="Parse"/> is the one bridge between them.
	/// </remarks>
	public enum TabPlacement
	{
		/// <summary>Tabs run left-to-right above the body; the selected tab is underlined.</summary>
		Top,

		/// <summary>Tabs stack top-to-bottom beside the body; the selected tab is marked on its leading edge.</summary>
		Left,
	}

	/// <summary>String bridge for the persisted <see cref="TabPlacement"/> setting.</summary>
	public static class TabPlacements
	{
		/// <summary>
		/// Read a stored placement string. ⚠️ Anything unrecognized — a hand-edited settings file, a value
		/// written by a future build, an empty string from a fresh file — falls back to
		/// <see cref="TabPlacement.Top"/> rather than throwing. A bad cosmetic preference must never be able
		/// to take down the settings window.
		/// </summary>
		public static TabPlacement Parse(string? value) =>
			Enum.TryParse<TabPlacement>(value, ignoreCase: true, out var placement) ? placement : TabPlacement.Top;
	}
}
