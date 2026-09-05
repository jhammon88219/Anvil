using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The DOW (mobile-radar) event LIBRARY: the offline-curated <c>.dow.json</c> frames
	/// (<c>tools/dow_import.py</c>) the DOW Event Viewer can show, served from the <c>dowevents</c>
	/// virtual host.
	/// </summary>
	/// <remarks>
	/// ⚠️ The library is a folder the app OWNS and writes to, not files sitting wherever the user left
	/// them, and that is forced by the WebView: <c>showDow</c> makes the page FETCH the frame, so it must
	/// be same-origin — i.e. under a mapped virtual host. An arbitrary <c>C:\…</c> path is unreachable.
	/// So <see cref="ImportAsync"/> copies a chosen file in, and the folder itself is the library.
	/// ⚠️ These frames are ~20 MB each and are NOT bundled with the app — the folder is empty on a fresh
	/// install and the viewer says so.
	/// </remarks>
	public interface IDowEventProvider
	{
		/// <summary>The frames currently in the library, by file name (may be empty).</summary>
		IReadOnlyList<DowEvent> GetEvents();

		/// <summary>
		/// Copies <paramref name="sourcePath"/> into the library and returns the imported frame. The
		/// source is left alone. A name collision gets a numeric suffix rather than overwriting — an
		/// import must never destroy a frame already in the library.
		/// </summary>
		Task<DowEvent> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

		/// <summary>Deletes one frame from the library. Ignores a file that is already gone.</summary>
		void Remove(string fileName);
	}
}
