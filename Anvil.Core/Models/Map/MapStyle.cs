namespace Anvil.Models
{
	/// <summary>
	/// A selectable map style — a Protomaps-schema MapLibre style document, either bundled with the app or
	/// imported by the user.
	/// </summary>
	/// <remarks>
	/// ⚠️ <see cref="Url"/> IS THE ONE PLACE A STYLE'S LOCATION IS DECIDED, and it exists because a style
	/// no longer always lives on the same host. Bundled styles are package content served over
	/// <c>mapassets</c>; imported ones are copies in a writable per-user folder served over
	/// <c>mapstyles</c> (see <c>MapStyleLibrary</c>). The URL used to be interpolated at each call site —
	/// twice in MapService and once more as a launch URL param — which is three places that would each
	/// have had to learn about the second host.
	/// ⚠️ <see cref="FileName"/> is still needed alongside it: the style EXPORT reads the pristine file off
	/// disk, and where that file is depends on <see cref="IsImported"/>.
	/// </remarks>
	public record MapStyle(string Id, string DisplayName, string FileName, string Url, bool IsImported = false);
}
