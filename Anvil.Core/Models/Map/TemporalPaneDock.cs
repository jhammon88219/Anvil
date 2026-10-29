using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// Which of a temporal pane's sections sit beside its rail (the DOCKED set), and in what order. Section ids are
	/// <see cref="Header"/> (the pane's header block) and the <see cref="LayerOrder"/> ids of the mode's OWN layer
	/// sections — ghost rows are never sections of a pane (they are full-pane only).
	/// </summary>
	/// <remarks>
	/// The dock stack keeps the FULL pane's order with the undocked sections left out: header first, then the layers in
	/// the user's layer order. The order is never stored here — it is always read from the layer order, so the two can't
	/// drift. ⚠️ The own-layer lists MIRROR the non-ghost <c>DragMoveLayerExpander</c>s of each mode's Content XAML.
	/// </remarks>
	public static class TemporalPaneDock
	{
		/// <summary>The header block's id (NowCast: header + warning tiles; PastCast: header + timeframe entry; ForeCast: header).</summary>
		public const string Header = "header";

		public static IReadOnlyList<string> NowCastLayers { get; } = new[]
		{
			LayerOrder.Cells, LayerOrder.Reports, LayerOrder.Warnings, LayerOrder.Watches,
			LayerOrder.Tropical, LayerOrder.Discussions, LayerOrder.Radar,
		};

		public static IReadOnlyList<string> PastCastLayers { get; } = new[]
		{
			LayerOrder.Cells, LayerOrder.Reports, LayerOrder.Damage, LayerOrder.Warnings, LayerOrder.Watches,
			LayerOrder.Tropical, LayerOrder.Discussions, LayerOrder.Outlook, LayerOrder.Radar,
		};

		public static IReadOnlyList<string> ForeCastLayers { get; } = new[] { LayerOrder.Outlook, LayerOrder.ModelField };

		/// <summary>
		/// The full pane's section order: <see cref="Header"/>, then the mode's own layers in <paramref name="layerOrder"/>
		/// (an own layer the order doesn't name lands where <see cref="LayerOrder.Complete"/> puts it — where the map draws it).
		/// </summary>
		public static List<string> FullOrder(IEnumerable<string>? layerOrder, IReadOnlyCollection<string> ownLayers)
		{
			var order = new List<string> { Header };
			order.AddRange(LayerOrder.Complete(layerOrder).Where(ownLayers.Contains));
			return order;
		}

		/// <summary>A saved docked set with unknown and repeated ids dropped (hand-edited or older settings).</summary>
		public static List<string> NormalizeDocked(IEnumerable<string>? ids, IReadOnlyCollection<string> ownLayers) =>
			(ids ?? Enumerable.Empty<string>())
				.Where(id => id == Header || ownLayers.Contains(id))
				.Distinct(StringComparer.Ordinal)
				.ToList();

		/// <summary>The dock stack, top first: <paramref name="fullOrder"/> with every undocked section left out.</summary>
		public static List<string> DockOrder(IEnumerable<string> fullOrder, IEnumerable<string>? docked)
		{
			var set = new HashSet<string>(docked ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
			return fullOrder.Where(set.Contains).ToList();
		}

		/// <summary>A rail sidecar's click: <paramref name="id"/> leaves the set if docked, else joins it.</summary>
		public static List<string> ToggleDocked(IEnumerable<string>? docked, string id)
		{
			var list = (docked ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
			if (!list.Remove(id)) { list.Add(id); }
			return list;
		}
	}
}
