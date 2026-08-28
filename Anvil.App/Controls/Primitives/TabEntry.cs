using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One tab in a <see cref="TabStrip"/>: what it shows (<see cref="Glyph"/> + <see cref="Label"/>), whether
	/// it can be picked (<see cref="IsEnabled"/>) and what state it is in (<see cref="IsSelected"/>, plus which
	/// edge its indicator sits on). The strip owns the SELECTION properties — a host builds entries with their
	/// content and never writes those — but <see cref="IsEnabled"/> and <see cref="Tooltip"/> belong to the
	/// host, which is the only thing that knows whether a tab currently applies.
	/// </summary>
	/// <remarks>
	/// The two <c>Show*</c> flags exist so the item template can bind them directly. A DataTemplate's x:Bind
	/// resolves against the ENTRY, not against the strip, so a template cannot ask its parent "am I vertical
	/// today?" — the strip pushes the answer down here instead, and the template just shows whichever
	/// indicator is live.
	///
	/// ⚠️ The content properties are <c>get; set;</c> and NOT <c>init</c>, however much they want to be: any
	/// public property of a type reachable from XAML gets a plain setter emitted into the generated
	/// XamlTypeInfo, and an init-only one fails that build with CS8852.
	/// </remarks>
	public sealed class TabEntry : ObservableObject
	{
		/// <summary>Segoe Fluent glyph shown above the label. ⚠️ Verify a codepoint by RENDERING it in the
		/// real font — a wrong one ships as an empty box (the same trap the bar keys documented).</summary>
		public string Glyph { get; set; } = "";

		/// <summary>The tab's name, shown under the glyph and used as the strip's width probe.</summary>
		public string Label { get; set; } = "";

		private string _tooltip = "";
		/// <summary>Hover text. Say what the tab holds, not what a tab is — and when the tab is DISABLED, say
		/// why and how to get it back. Notifying, because that text changes with availability.</summary>
		public string Tooltip
		{
			get => _tooltip;
			set => SetProperty(ref _tooltip, value);
		}

		private bool _isEnabled = true;
		/// <summary>Whether this tab can be picked. False greys it (the shared style has Disabled and
		/// CheckedDisabled states) and takes it out of hit-testing and the tab order, while leaving it in the
		/// strip — a tab that vanished would reflow the rail and hide the fact that the feature exists.
		/// ⚠️ Written by the HOST, not the strip. The strip will not move the selection off a tab that goes
		/// disabled; whoever disabled it knows where the selection should land instead.</summary>
		public bool IsEnabled
		{
			get => _isEnabled;
			set => SetProperty(ref _isEnabled, value);
		}

		private bool _isSelected;
		/// <summary>Whether this is the strip's current tab. Written by the strip only.</summary>
		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (SetProperty(ref _isSelected, value))
				{
					OnPropertyChanged(nameof(ShowUnderline));
					OnPropertyChanged(nameof(ShowSideBar));
				}
			}
		}

		private bool _isVertical;
		/// <summary>Whether the strip is currently a SIDE rail. Written by the strip only; it decides which
		/// of the two indicators this entry shows.</summary>
		public bool IsVertical
		{
			get => _isVertical;
			set
			{
				if (SetProperty(ref _isVertical, value))
				{
					OnPropertyChanged(nameof(ShowUnderline));
					OnPropertyChanged(nameof(ShowSideBar));
				}
			}
		}

		/// <summary>Selected in a TOP rail: the indicator is an underline on the bottom edge.</summary>
		public bool ShowUnderline => _isSelected && !_isVertical;

		/// <summary>Selected in a SIDE rail: the indicator is a bar on the leading edge.</summary>
		public bool ShowSideBar => _isSelected && _isVertical;
	}
}
