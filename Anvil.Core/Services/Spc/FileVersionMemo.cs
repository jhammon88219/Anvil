using System;
using System.Collections.Generic;
using System.IO;

namespace Anvil.Services
{
	/// <summary>
	/// Remembers what a reader made of a file, per FILE VERSION (last write time + length), so a file read over and over
	/// is parsed once per change. Built for <see cref="SpcOutlookService"/>'s cache readers, which the outlook VMs call
	/// synchronously on the UI thread on every legend/header refresh (perf audit issue 5, 2026-10-12).
	/// </summary>
	/// <remarks>
	/// ⚠️ Only correct for files REPLACED by a temp + move or rewritten (both stamp a new write time); a missing file is
	/// never remembered. ⚠️ The value is shared between callers — store immutable results only (records, read-only sets).
	/// A file swapped between the stat and the read is stored under the OLD stamp, so the next call re-reads: self-healing.
	/// </remarks>
	internal sealed class FileVersionMemo
	{
		private readonly object _gate = new();
		private readonly Dictionary<(string Kind, string Path), (DateTime Stamp, long Length, object? Value)> _reads = new();

		public T Get<T>(string kind, string path, Func<T> read)
		{
			FileInfo info;
			try
			{
				info = new FileInfo(path);
				if (!info.Exists) { return read(); }
			}
			catch
			{
				return read();
			}
			var key = (kind, path);
			lock (_gate)
			{
				if (_reads.TryGetValue(key, out var hit) && hit.Stamp == info.LastWriteTimeUtc && hit.Length == info.Length)
				{
					return (T)hit.Value!;
				}
			}
			var value = read();
			lock (_gate) { _reads[key] = (info.LastWriteTimeUtc, info.Length, value); }
			return value;
		}
	}
}
