using System;
using System.Collections.Generic;
using System.Globalization;

namespace Anvil.Services
{
	/// <summary>
	/// A model file's <c>.idx</c> — one line per GRIB2 message, <c>n:offset:d=YYYYMMDDHH:VAR:LEVEL:TIME:</c> — and the
	/// byte range of the message a (variable, level, time) names. Pure.
	/// </summary>
	/// <remarks>
	/// A message runs from its offset to the NEXT line's offset − 1; the last one runs to the end of the file
	/// (<see cref="Range.Length"/> null = an open "bytes=offset-" request).
	/// </remarks>
	internal static class HrrrIndex
	{
		public readonly record struct Range(long Offset, long? Length);

		public static Range? Find(string idxText, string variable, string level, string time)
		{
			var lines = Parse(idxText);
			for (var k = 0; k < lines.Count; k++)
			{
				var e = lines[k];
				if (e.Variable != variable || e.Level != level || e.Time != time) continue;
				return new Range(e.Offset, k + 1 < lines.Count ? lines[k + 1].Offset - e.Offset : null);
			}
			return null;
		}

		private readonly record struct Entry(long Offset, string Variable, string Level, string Time);

		private static List<Entry> Parse(string text)
		{
			var list = new List<Entry>();
			foreach (var raw in text.Split('\n'))
			{
				var parts = raw.TrimEnd('\r').Split(':');
				if (parts.Length < 6 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) continue;
				list.Add(new Entry(offset, parts[3], parts[4], parts[5]));
			}
			return list;
		}
	}
}
