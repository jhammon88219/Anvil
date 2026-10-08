using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// A loop FRAME's key: an archive VOLUME key plus which 0.5° PASS of it. Pass 1 (the volume start) is the plain
	/// volume key — byte-identical to every key before rescans were frames, so caches, logs and the live paths are
	/// untouched; a SAILS / MRLE rescan is <c>"{volumeKey}#{pass}"</c> (pass 2..). The loop engine keeps its one model
	/// — frame i is keys[i] — and only code that talks to S3 or the cache asks <see cref="VolumeKey"/>.
	/// </summary>
	/// <remarks>Why a string and not a record: the engine, the cache prune, the incremental reload and the load-time log
	/// all carry frame identity as the key string, and a pass is one more fact about that identity, not a new kind of
	/// frame. '#' never occurs in an S3 volume key.</remarks>
	public static class RadarFrameKey
	{
		private const char Separator = '#';

		/// <summary>The frame key for <paramref name="pass"/> (1-based) of <paramref name="volumeKey"/>.</summary>
		public static string Of(string volumeKey, int pass) =>
			pass <= 1 ? volumeKey : volumeKey + Separator + pass.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>The archive volume key a frame key belongs to (what S3 and the cache prune know).</summary>
		public static string VolumeKey(string frameKey)
		{
			var i = frameKey.IndexOf(Separator);
			return i < 0 ? frameKey : frameKey[..i];
		}

		/// <summary>The 1-based 0.5° pass a frame key names (1 = the volume start).</summary>
		public static int Pass(string frameKey)
		{
			var i = frameKey.IndexOf(Separator);
			return i >= 0 && int.TryParse(frameKey.AsSpan(i + 1), System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture, out var p) && p >= 1 ? p : 1;
		}

		/// <summary>Volume keys → frame keys, every volume's passes in scan order (oldest first), given each volume's
		/// planned pass count (a missing count = 1).</summary>
		public static List<string> Expand(IReadOnlyList<string> volumeKeys, IReadOnlyDictionary<string, int> passes)
		{
			var frames = new List<string>(volumeKeys.Count);
			foreach (var key in volumeKeys)
			{
				var n = passes.TryGetValue(key, out var c) ? Math.Max(1, c) : 1;
				for (var p = 1; p <= n; p++) frames.Add(Of(key, p));
			}
			return frames;
		}
	}
}
