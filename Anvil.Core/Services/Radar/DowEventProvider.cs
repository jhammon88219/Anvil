using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IDowEventProvider"/>. The library is a per-user folder under
	/// <c>%LocalAppData%\Anvil\DowEvents</c>, mapped to the <see cref="HostName"/> virtual host, holding
	/// <c>.dow.json</c> frames the user has imported. No parsing happens here — the label comes from the
	/// file name, and the WebView fetches + decodes the frame only when it is loaded.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>The library moved OUT of the app package (2026-09-04) and that was the point of the change.</b>
	/// It used to be <c>AppContext.BaseDirectory\Assets\DowEvents</c>, which is INSIDE the installed MSIX and
	/// therefore READ-ONLY — so frames could only arrive by being committed to the repo and packaged, and a
	/// ~20 MB sample sitting in that folder was built into every Debug AND Release package. Nothing can be
	/// imported into a read-only folder, so the folder had to move before a picker could exist at all.
	/// ⚠️ Consequence, intended: the app ships with an EMPTY library. There are no bundled events any more.
	///
	/// ⚠️ <b>DON'T try to skip the copy by re-mapping the host at the file's own folder.</b> It looks like the
	/// obvious optimisation — <c>SetVirtualHostNameToFolderMapping</c> can be called again at any time — but
	/// WebView2 documents the catch: <i>"As the resource loaders for the current page might have already been
	/// created and running, changes to the mapping might not be applied to the current page and a reload of
	/// the page is needed to apply the new mapping."</i> Our map page is loaded once and lives for the whole
	/// session, so a reload means tearing down the map, its panes and any loaded loop. The copy is ~20 MB to
	/// a local disk, once, at import; that is far cheaper than the alternative.
	/// </remarks>
	public sealed class DowEventProvider : IDowEventProvider
	{
		private const string FrameExtension = ".dow.json";

		/// <summary>WebView2 virtual host the library folder is mapped to (see MainWindow).</summary>
		public const string HostName = "dowevents";

		/// <summary>The per-user library folder. Created on demand; safe to call before it exists.</summary>
		public static string EventsDirectory => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Anvil", "DowEvents");

		public IReadOnlyList<DowEvent> GetEvents()
		{
			var dir = EventsDirectory;
			if (!Directory.Exists(dir))
			{
				return Array.Empty<DowEvent>();
			}

			return Directory.EnumerateFiles(dir, "*" + FrameExtension)
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.Select(f => EventFor(Path.GetFileName(f)))
				.ToList();
		}

		public async Task<DowEvent> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
			{
				throw new FileNotFoundException("No such DOW frame file.", sourcePath);
			}

			var dir = EventsDirectory;
			Directory.CreateDirectory(dir);

			var target = Path.Combine(dir, UniqueNameFor(dir, Path.GetFileName(sourcePath)));

			// Streamed rather than File.Copy so the ~20 MB read/write is cancellable and stays off the
			// caller's thread. FileShare.Read on the source: the user's own copy stays readable meanwhile.
			using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
			using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
			{
				await src.CopyToAsync(dst, cancellationToken);
			}

			return EventFor(Path.GetFileName(target));
		}

		public void Remove(string fileName)
		{
			if (string.IsNullOrWhiteSpace(fileName))
			{
				return;
			}

			// Defend the library folder against a path escaping into it: only ever delete a BARE name we
			// re-combine ourselves, never a caller-supplied path.
			var bare = Path.GetFileName(fileName);
			try
			{
				File.Delete(Path.Combine(EventsDirectory, bare));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best effort: a frame held open by the WebView stays until the next launch.
			}
		}

		private static DowEvent EventFor(string fileName) =>
			new(fileName, LabelFor(fileName), $"https://{HostName}/{Uri.EscapeDataString(fileName)}");

		// "name.dow.json" -> "name (2).dow.json" when taken. An import must never overwrite.
		private static string UniqueNameFor(string dir, string fileName)
		{
			if (!File.Exists(Path.Combine(dir, fileName)))
			{
				return fileName;
			}

			var stem = StemOf(fileName);
			for (var n = 2; n < 1000; n++)
			{
				var candidate = $"{stem} ({n}){FrameExtension}";
				if (!File.Exists(Path.Combine(dir, candidate)))
				{
					return candidate;
				}
			}
			return $"{stem} ({Guid.NewGuid():N}){FrameExtension}";
		}

		// Human label from the file name (the converter names files meaningfully, e.g.
		// "goshen_2009-06-05_DOW7.dow.json"). Strips the suffix and tidies separators.
		// (Reading the frame's "event" field for a nicer label is a future refinement.)
		private static string LabelFor(string fileName) => StemOf(fileName).Replace('_', ' ').Trim();

		private static string StemOf(string fileName) =>
			fileName.EndsWith(FrameExtension, StringComparison.OrdinalIgnoreCase)
				? fileName[..^FrameExtension.Length]
				: Path.GetFileNameWithoutExtension(fileName);
	}
}
