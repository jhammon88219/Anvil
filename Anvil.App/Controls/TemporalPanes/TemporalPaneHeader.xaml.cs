using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>
	/// The name row every temporal pane opens with (see TemporalPaneHeader.xaml): status block · title over
	/// subline · hairline · <see cref="Readout"/>. The same control in all three windows; only the contents differ.
	/// </summary>
	[ContentProperty(Name = nameof(Readout))]
	public sealed partial class TemporalPaneHeader : UserControl
	{
		public TemporalPaneHeader()
		{
			InitializeComponent();
		}

		/// <summary>The status block's colour — DATA (a literal), never a theme brush.</summary>
		public Brush? StatusFill
		{
			get => (Brush?)GetValue(StatusFillProperty);
			set => SetValue(StatusFillProperty, value);
		}
		public static readonly DependencyProperty StatusFillProperty =
			DependencyProperty.Register(nameof(StatusFill), typeof(Brush), typeof(TemporalPaneHeader), new PropertyMetadata(null));

		/// <summary>What the status colour means, in words.</summary>
		public string? StatusToolTip
		{
			get => (string?)GetValue(StatusToolTipProperty);
			set => SetValue(StatusToolTipProperty, value);
		}
		public static readonly DependencyProperty StatusToolTipProperty =
			DependencyProperty.Register(nameof(StatusToolTip), typeof(string), typeof(TemporalPaneHeader), new PropertyMetadata(null));

		/// <summary>The window's name: "NowCast" / "PastCast" / "ForeCast".</summary>
		public string Title
		{
			get => (string)GetValue(TitleProperty);
			set => SetValue(TitleProperty, value);
		}
		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register(nameof(Title), typeof(string), typeof(TemporalPaneHeader), new PropertyMetadata(""));

		/// <summary>The line under the title (a date or day).</summary>
		public string Subtitle
		{
			get => (string)GetValue(SubtitleProperty);
			set => SetValue(SubtitleProperty, value);
		}
		public static readonly DependencyProperty SubtitleProperty =
			DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(TemporalPaneHeader), new PropertyMetadata(""));

		/// <summary>The subline's opacity — 0.4 for a placeholder (PastCast with nothing loaded).</summary>
		public double SubtitleOpacity
		{
			get => (double)GetValue(SubtitleOpacityProperty);
			set => SetValue(SubtitleOpacityProperty, value);
		}
		public static readonly DependencyProperty SubtitleOpacityProperty =
			DependencyProperty.Register(nameof(SubtitleOpacity), typeof(double), typeof(TemporalPaneHeader), new PropertyMetadata(1.0));

		/// <summary>The right slot: the mode's TemporalPaneHeader…Readout control.</summary>
		public object? Readout
		{
			get => GetValue(ReadoutProperty);
			set => SetValue(ReadoutProperty, value);
		}
		public static readonly DependencyProperty ReadoutProperty =
			DependencyProperty.Register(nameof(Readout), typeof(object), typeof(TemporalPaneHeader), new PropertyMetadata(null));
	}
}
