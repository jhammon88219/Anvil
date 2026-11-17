using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One row in a <see cref="TabStrip"/> (the left nav rail): what it shows (<see cref="Glyph"/> +
	/// <see cref="Label"/>), whether it can be picked (<see cref="IsEnabled"/>) and whether it is the current
	/// one (<see cref="IsSelected"/>). The strip owns <see cref="IsSelected"/> — a host builds entries with
	/// their content and never writes it — but <see cref="IsEnabled"/> and <see cref="Tooltip"/> belong to the
	/// host, which is the only thing that knows whether a page currently applies.
	/// </summary>
	/// <remarks>
	/// ⚠️ The content properties are <c>get; set;</c> and NOT <c>init</c>, however much they want to be: any
	/// public property of a type reachable from XAML gets a plain setter emitted into the generated
	/// XamlTypeInfo, and an init-only one fails that build with CS8852.
	/// </remarks>
	public sealed class TabEntry : ObservableObject
	{
		/// <summary>Segoe Fluent glyph shown beside the label. ⚠️ Verify a codepoint by RENDERING it in the
		/// real font — a wrong one ships as an empty box (the same trap the bar keys documented).</summary>
		public string Glyph { get; set; } = "";

		/// <summary>The row's name. Also the text of the matching item in the top bar's menu.</summary>
		public string Label { get; set; } = "";

		/// <summary>A rule is drawn ABOVE this row (Settings' Dev entry, which sits apart from the rest).</summary>
		public bool IsSeparated { get; set; }

		/// <summary>
		/// Which page this row selects (the strip's <see cref="TabStrip.SelectedIndex"/>). -1 (default) = its POSITION in the
		/// list. Set it when the rail's display order differs from the page numbers — Settings lists General first though it
		/// is page 6, and leaves retired pages out (the page number is the persisted index, so it can't be renumbered).
		/// </summary>
		public int PageIndex { get; set; } = -1;

		private string _tooltip = "";
		/// <summary>Hover text. Say what the page holds — and when the row is DISABLED, say why and how to get it
		/// back. Notifying, because that text changes with availability.</summary>
		public string Tooltip
		{
			get => _tooltip;
			set => SetProperty(ref _tooltip, value);
		}

		private bool _isEnabled = true;
		/// <summary>Whether this row can be picked. False greys it and takes it out of hit-testing and the tab
		/// order, while leaving it in the rail. ⚠️ Written by the HOST, not the strip.</summary>
		public bool IsEnabled
		{
			get => _isEnabled;
			set => SetProperty(ref _isEnabled, value);
		}

		private bool _joinsBody;
		/// <summary>The strip's <see cref="TabStrip.JoinsBody"/>, copied onto every row so the item template can read it.
		/// Written by the strip only.</summary>
		public bool JoinsBody
		{
			get => _joinsBody;
			set => SetProperty(ref _joinsBody, value);
		}

		private bool _isSelected;
		/// <summary>Whether this is the rail's current row. Written by the strip only.</summary>
		public bool IsSelected
		{
			get => _isSelected;
			set => SetProperty(ref _isSelected, value);
		}
	}
}
