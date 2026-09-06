namespace Anvil.Models
{
	/// <summary>
	/// Which of WinUI's two built-in palettes a theme is BUILT ON.
	/// </summary>
	/// <remarks>
	/// ⚠️ A theme does NOT re-author WinUI's palette — there are several hundred system brushes (text
	/// ranks, card strokes, disabled states, focus rects, every stock control) and re-declaring them is
	/// not a thing anyone finishes. A theme picks a base, so all of that resolves coherently for free,
	/// and then overrides the small named set it actually owns.
	/// ⚠️ A CORE enum, deliberately: <c>ElementTheme</c> is WinUI and Anvil.Core must never gain a Windows
	/// dependency. Anvil.App maps this to <c>ElementTheme</c> at the edge, the same seam every other
	/// platform-bound type crosses.
	/// </remarks>
	public enum ThemeBase
	{
		Light,
		Dark
	}

	/// <summary>
	/// One visual identity — the app chrome and the basemap under it, named and chosen together.
	/// </summary>
	/// <remarks>
	/// THE MEMBERSHIP RULE, and the first one that will get violated: a theme owns CHROME, never DATA.
	/// Chrome is the OverlayBar surfaces, the panel palette, the accent, the basemap style, the map-drawn
	/// furniture (the isolation mask, the site keys, leader lines). Data is the NWS reflectivity bands,
	/// every other product ramp, SPC's embedded outlook colors, TO=red / SV=yellow, the storm-report dots,
	/// the green/red site availability square. Those carry MEANING that has to survive a theme switch, or
	/// the legend stops being a legend. If a color tells you what something IS, it isn't the theme's.
	///
	/// ⚠️ This is a MANIFEST, not a palette bag. It holds what code has to ACT on — which base to resolve
	/// against, which basemap to load — and the color values live where their consumer can already resolve
	/// them (Controls/Styles.xaml for the WinUI side). A C# type holding brushes would mean every control
	/// binding a brush property instead of {ThemeResource}, and losing high-contrast for nothing.
	///
	/// <paramref name="MapStyleId"/> is an <c>IStyleProvider</c> style id, not a file name — the style
	/// list stays the one place a file name is written down.
	///
	/// ⚠️ <paramref name="GroundColor"/> is the ONE color on the manifest, and it is here because the host
	/// has to paint it BEFORE any palette exists: it is the WebView2 <c>DefaultBackgroundColor</c>, shown
	/// in the gap before the page's first paint (the no-white-flash launch) and again if the renderer ever
	/// dies. C# cannot read the page's CSS, so this value is written TWICE — here and as
	/// <c>--anvil-ground</c> in <c>Assets/Map/theme.css</c> — and the two must agree. Same deal as the pane
	/// arrangement living in both MainWindow.xaml and map.js <c>paneRects</c>: change both.
	/// ⚠️ It is not a precedent for a second color. Everything else has a consumer that can resolve its own
	/// palette; if a value looks like it wants to live here, check that first.
	/// </remarks>
	public record AppTheme(string Id, string DisplayName, ThemeBase Base, string MapStyleId, string GroundColor);
}
