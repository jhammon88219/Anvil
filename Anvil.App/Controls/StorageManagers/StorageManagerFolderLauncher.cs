using System;
using System.Diagnostics;
using System.IO;
using Windows.Storage;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>
	/// Opens one of Anvil's folders in Explorer for Settings → Storage (the page's "Open Anvil's folder" and each
	/// row's folder button).
	/// </summary>
	/// <remarks>
	/// ⚠️ PACKAGED-APP PATHS: Anvil runs as an MSIX, so what it writes under <c>%LocalAppData%\Anvil</c> really lands in
	/// <c>%LocalAppData%\Packages\&lt;pfn&gt;\LocalCache\Local\Anvil</c>. Explorer runs OUTSIDE the package and would
	/// see the unredirected path (missing, or empty). So a path under LocalAppData is mapped onto the package's
	/// LocalCache first, when that folder exists; anything else (a loose Debug exe, a path outside) opens as given.
	/// </remarks>
	internal static class StorageManagerFolderLauncher
	{
		public static void Open(string path)
		{
			var target = RealPath(path);
			try
			{
				Directory.CreateDirectory(target); // an empty kind's folder may not exist yet
				Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
			}
			catch (Exception ex) { Debug.WriteLine($"[storage] Explorer failed for {target}: {ex.Message}"); }
		}

		private static string RealPath(string path)
		{
			var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!path.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return path;
			try
			{
				var cache = ApplicationData.Current.LocalCacheFolder.Path;
				var mapped = Path.Combine(cache, "Local", path.Substring(local.Length).TrimStart('\\', '/'));
				var anvilRoot = Path.Combine(cache, "Local", "Anvil");
				if (Directory.Exists(anvilRoot)) return mapped;
			}
			catch (InvalidOperationException) { /* not packaged */ }
			catch (Exception ex) { Debug.WriteLine($"[storage] LocalCache lookup failed: {ex.Message}"); }
			return path;
		}
	}
}
