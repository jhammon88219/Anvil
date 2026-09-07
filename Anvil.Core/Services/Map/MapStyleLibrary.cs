using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>The user's imported basemap styles: enumerate, import, remove.</summary>
	public interface IMapStyleLibrary
	{
		/// <summary>Every imported style, name-ordered. Empty when nothing has been imported.</summary>
		IReadOnlyList<MapStyle> GetStyles();

		/// <summary>
		/// Copies a style document into the library and returns it. Throws when the file is missing or is
		/// not a usable MapLibre style — see <c>MapStyleLibrary</c> for what "usable" is checked to mean.
		/// </summary>
		Task<MapStyle> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

		/// <summary>Deletes an imported style. Silently does nothing if it is already gone.</summary>
		void Remove(string fileName);
	}

	/// <summary>
	/// The imported-style library: <c>%LocalAppData%\Anvil\MapStyles</c>, served to the WebView over the
	/// <see cref="HostName"/> virtual host.
	/// </summary>
	/// <remarks>
	/// ⚠️ IMPORT COPIES, AND THE COPY IS NOT AVOIDABLE. The page FETCHES the style document, so it has to
	/// sit under a mapped virtual host — and the bundled one (<c>mapassets</c>) points into the MSIX
	/// package, which is read-only. Pointing a host at wherever the user's file happens to live is not an
	/// option either: <c>SetVirtualHostNameToFolderMapping</c> only takes effect after a page RELOAD, and
	/// reloading means tearing down the map, its panes and any loaded loop. Same reasoning, same shape as
	/// <see cref="DowEventProvider"/> — which learned it the hard way, by shipping its library inside the
	/// package and finding importing impossible.
	///
	/// ⚠️ VALIDATION IS DELIBERATELY SHALLOW: parse as JSON, require a <c>layers</c> array and a
	/// <c>sources</c> object. That catches the file that is not a style at all — which matters, because a
	/// malformed style reaches <c>setStyle</c> and can leave the map blank. It does NOT catch the deeper
	/// problem: these are Protomaps-SCHEMA styles, and <c>map.js tileSourceFor</c> patches THE FIRST SOURCE
	/// to point at the offline archive. A well-formed style built for another vendor's tiles will import,
	/// select, and render nothing. Validating that would mean knowing the schema's layer vocabulary, which
	/// is a bigger promise than this makes.
	/// </remarks>
	public sealed class MapStyleLibrary : IMapStyleLibrary
	{
		/// <summary>WebView2 virtual host the library folder is mapped to (see MainWindow).</summary>
		public const string HostName = "mapstyles";

		/// <summary>The per-user library folder. Created on demand; safe to read before it exists.</summary>
		public static string StylesDirectory => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Anvil", "MapStyles");

		public IReadOnlyList<MapStyle> GetStyles()
		{
			var dir = StylesDirectory;
			if (!Directory.Exists(dir))
			{
				return Array.Empty<MapStyle>();
			}

			return Directory.EnumerateFiles(dir, "*.json")
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.Select(f => StyleFor(Path.GetFileName(f)))
				.ToList();
		}

		public async Task<MapStyle> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
			{
				throw new FileNotFoundException("No such style file.", sourcePath);
			}

			var text = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
			Validate(text);

			var dir = StylesDirectory;
			Directory.CreateDirectory(dir);

			// ⚠️ Never overwrite: importing the same name twice gives "name (2).json" rather than replacing
			// a style the user may still have selected. Removing one is an explicit action.
			var target = Path.Combine(dir, UniqueNameFor(dir, Path.GetFileName(sourcePath)));
			File.Copy(sourcePath, target);

			return StyleFor(Path.GetFileName(target));
		}

		public void Remove(string fileName)
		{
			if (string.IsNullOrWhiteSpace(fileName))
			{
				return;
			}

			try
			{
				// GetFileName strips any path the caller may have handed us, so this can only ever delete
				// inside the library folder.
				File.Delete(Path.Combine(StylesDirectory, Path.GetFileName(fileName)));
			}
			catch (IOException) { /* in use; the next enumerate still shows it, which is honest */ }
			catch (UnauthorizedAccessException) { /* same */ }
		}

		// The shallow check. Throws with a message meant to be shown to the user, not logged.
		private static void Validate(string text)
		{
			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(text);
			}
			catch (JsonException)
			{
				throw new InvalidDataException("That file isn't valid JSON.");
			}

			using (doc)
			{
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
				{
					throw new InvalidDataException("That file has no \"layers\" array, so it isn't a map style.");
				}

				if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object)
				{
					throw new InvalidDataException("That style has no \"sources\", so nothing would draw.");
				}
			}
		}

		private static MapStyle StyleFor(string fileName)
		{
			var display = Path.GetFileNameWithoutExtension(fileName);
			return new MapStyle(
				Id: "imported:" + fileName,
				DisplayName: display,
				FileName: fileName,
				Url: $"https://{HostName}/{Uri.EscapeDataString(fileName)}",
				IsImported: true);
		}

		private static string UniqueNameFor(string dir, string fileName)
		{
			if (!File.Exists(Path.Combine(dir, fileName)))
			{
				return fileName;
			}

			var bare = Path.GetFileNameWithoutExtension(fileName);
			var ext = Path.GetExtension(fileName);
			for (var n = 2; ; n++)
			{
				var candidate = $"{bare} ({n}){ext}";
				if (!File.Exists(Path.Combine(dir, candidate)))
				{
					return candidate;
				}
			}
		}
	}
}
