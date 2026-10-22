using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Brings a bound row collection in step with a new list using the FEWEST edits (remove, move, insert) — an
	/// unchanged list gets none. ⚠️ Why: replacing a ListView's ItemsSource (or Clear + Add) rebuilds every row, and
	/// a live feed refreshing every few seconds made the storm-report list flash (2026-10-21). Rows are matched by
	/// KEY, so a kept row keeps its object (and any state on it).
	/// </summary>
	public static class RowSync
	{
		/// <param name="rows">The bound collection, edited in place.</param>
		/// <param name="target">The new list, in display order (keys unique or not — duplicates pair up in order).</param>
		/// <param name="keyOf">A row's key (value equality).</param>
		/// <param name="make">A row for a key that has none yet.</param>
		public static void Sync<TRow, TKey>(ObservableCollection<TRow> rows, IReadOnlyList<TKey> target,
			Func<TRow, TKey> keyOf, Func<TKey, TRow> make) where TKey : notnull
		{
			var cmp = EqualityComparer<TKey>.Default;
			var keep = new HashSet<TKey>(target);
			for (var i = rows.Count - 1; i >= 0; i--)
			{
				if (!keep.Contains(keyOf(rows[i]))) { rows.RemoveAt(i); }
			}
			for (var i = 0; i < target.Count; i++)
			{
				if (i < rows.Count && cmp.Equals(keyOf(rows[i]), target[i])) { continue; }
				var at = -1;
				for (var j = i + 1; j < rows.Count; j++) { if (cmp.Equals(keyOf(rows[j]), target[i])) { at = j; break; } }
				if (at >= 0) { rows.Move(at, i); } else { rows.Insert(i, make(target[i])); }
			}
			while (rows.Count > target.Count) { rows.RemoveAt(rows.Count - 1); }
		}
	}
}
