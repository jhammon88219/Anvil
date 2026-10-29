using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.TemporalWindows.DragMoveLayerExpanders
{
	/// <summary>
	/// The summary card at the top of a <see cref="DragMoveLayerExpander"/> body, flush under its header (see the
	/// XAML header). Plain text DPs on ONE line: Headline left, Context right — or Footer in Context's place while it
	/// has text.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderHeaderCard : UserControl
	{
		public DragMoveLayerExpanderHeaderCard()
		{
			InitializeComponent();
		}

		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// The right slot: Context, unless a Footer has taken it.
		public Visibility ShowContext(string? footer) =>
			string.IsNullOrEmpty(footer) ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The left of the line — what is on the map ("2 active warnings").</summary>
		public string Headline
		{
			get => (string)GetValue(HeadlineProperty);
			set => SetValue(HeadlineProperty, value);
		}

		public static readonly DependencyProperty HeadlineProperty =
			DependencyProperty.Register(nameof(Headline), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));

		/// <summary>The right of the line (which day / scan / moment, when updated).</summary>
		public string Context
		{
			get => (string)GetValue(ContextProperty);
			set => SetValue(ContextProperty, value);
		}

		public static readonly DependencyProperty ContextProperty =
			DependencyProperty.Register(nameof(Context), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));

		/// <summary>Loading and failures — shown in Context's place while it has text.</summary>
		public string Footer
		{
			get => (string)GetValue(FooterProperty);
			set => SetValue(FooterProperty, value);
		}

		public static readonly DependencyProperty FooterProperty =
			DependencyProperty.Register(nameof(Footer), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));
	}
}
