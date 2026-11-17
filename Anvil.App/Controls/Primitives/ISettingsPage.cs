namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A Settings page body (Composites/&lt;Page&gt;SettingsTab). The page's "Reset all &lt;Page&gt; settings" key lives in the
	/// Settings window's PINNED page header (beside the title, 2026-11-17), not on the page — the window calls the shown
	/// page's <see cref="ResetAll"/>, which ASKS first (Dialogs/ConfirmResetDialog) and then resets. A page with nothing to
	/// reset (Dev) simply isn't one, and the key hides.
	/// </summary>
	public interface ISettingsPage
	{
		/// <summary>What Reset all puts back, for the key's tooltip.</summary>
		string ResetAllToolTip { get; }

		/// <summary>Ask first, then reset every setting on the page.</summary>
		void ResetAll();
	}
}
