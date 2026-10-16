using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.TemporalWindows.DragMoveLayerExpanders
{
	/// <summary>
	/// The summary card at the top of a <see cref="DragMoveLayerExpander"/> body, flush under its header (see the
	/// XAML header). Plain text DPs; each line but the headline collapses when empty.
	/// </summary>
	public sealed partial class DragMoveLayerExpanderHeaderCard : UserControl
	{
		public DragMoveLayerExpanderHeaderCard()
		{
			InitializeComponent();
		}

		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>The big first line — what is on the map ("2 active warnings").</summary>
		public string Headline
		{
			get => (string)GetValue(HeadlineProperty);
			set => SetValue(HeadlineProperty, value);
		}

		public static readonly DependencyProperty HeadlineProperty =
			DependencyProperty.Register(nameof(Headline), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));

		/// <summary>Optional emphasised line (warnings' elevated tags).</summary>
		public string Threats
		{
			get => (string)GetValue(ThreatsProperty);
			set => SetValue(ThreatsProperty, value);
		}

		public static readonly DependencyProperty ThreatsProperty =
			DependencyProperty.Register(nameof(Threats), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));

		/// <summary>Optional secondary line (which day / scan / moment, when updated).</summary>
		public string Context
		{
			get => (string)GetValue(ContextProperty);
			set => SetValue(ContextProperty, value);
		}

		public static readonly DependencyProperty ContextProperty =
			DependencyProperty.Register(nameof(Context), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));

		/// <summary>Optional small line — loading and failures.</summary>
		public string Footer
		{
			get => (string)GetValue(FooterProperty);
			set => SetValue(FooterProperty, value);
		}

		public static readonly DependencyProperty FooterProperty =
			DependencyProperty.Register(nameof(Footer), typeof(string), typeof(DragMoveLayerExpanderHeaderCard), new PropertyMetadata(""));
	}
}
