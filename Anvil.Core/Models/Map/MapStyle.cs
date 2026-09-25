namespace Anvil.Models
{
	/// <summary>
	/// A selectable map style — one of the five bundled Protomaps-schema MapLibre style documents.
	/// </summary>
	/// <remarks>
	/// ⚠️ <see cref="Url"/> IS THE ONE PLACE A STYLE'S LOCATION IS DECIDED. Today every style is package
	/// content on the <c>mapassets</c> host, so the value has exactly one shape — but it is kept as a field
	/// rather than interpolated because it was in THREE call sites before (twice in MapService, once as the
	/// page's launch URL param), and the page's <c>style</c> param takes a full URL as a result.
	/// ⚠️ <see cref="FileName"/> is the file under <c>Assets/Map</c>; <see cref="Id"/> is what
	/// <c>AppTheme.MapStyleId</c> matches against.
	/// ⚠️ <see cref="HasCountyLines"/> is a hand-kept fact about the FILE (a <c>boundaries_county</c> layer):
	/// it greys the Map flyout's Counties row on the styles that have none.
	/// </remarks>
	public record MapStyle(string Id, string DisplayName, string FileName, string Url, bool HasCountyLines = false);
}
