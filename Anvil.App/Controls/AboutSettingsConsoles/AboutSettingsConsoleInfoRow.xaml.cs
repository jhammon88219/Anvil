using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.AboutSettingsConsoles
{
	/// <summary>One fact on Settings → About (see the XAML header): Label · Value · an optional Action key.</summary>
	public sealed partial class AboutSettingsConsoleInfoRow : UserControl
	{
		public AboutSettingsConsoleInfoRow()
		{
			InitializeComponent();
		}

		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}

		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(AboutSettingsConsoleInfoRow), new PropertyMetadata(""));

		public string Value
		{
			get => (string)GetValue(ValueProperty);
			set => SetValue(ValueProperty, value);
		}

		public static readonly DependencyProperty ValueProperty =
			DependencyProperty.Register(nameof(Value), typeof(string), typeof(AboutSettingsConsoleInfoRow), new PropertyMetadata(""));

		/// <summary>Draw the value in the quiet Caption grey (a source line, a path) instead of Body.</summary>
		public bool IsQuiet
		{
			get => (bool)GetValue(IsQuietProperty);
			set => SetValue(IsQuietProperty, value);
		}

		public static readonly DependencyProperty IsQuietProperty =
			DependencyProperty.Register(nameof(IsQuiet), typeof(bool), typeof(AboutSettingsConsoleInfoRow), new PropertyMetadata(false));

		/// <summary>A key at the far right (Open, Read it…). Null = none.</summary>
		public object? Action
		{
			get => GetValue(ActionProperty);
			set => SetValue(ActionProperty, value);
		}

		public static readonly DependencyProperty ActionProperty =
			DependencyProperty.Register(nameof(Action), typeof(object), typeof(AboutSettingsConsoleInfoRow), new PropertyMetadata(null));

		/// <summary>Set the (quiet) value against the row's RIGHT edge instead of after the label (Data sources).</summary>
		public bool IsValueRight
		{
			get => (bool)GetValue(IsValueRightProperty);
			set => SetValue(IsValueRightProperty, value);
		}

		public static readonly DependencyProperty IsValueRightProperty =
			DependencyProperty.Register(nameof(IsValueRight), typeof(bool), typeof(AboutSettingsConsoleInfoRow), new PropertyMetadata(false));

		public Visibility Show(bool value, bool when) => value == when ? Visibility.Visible : Visibility.Collapsed;

		public HorizontalAlignment ValueAlignment(bool right) => right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;

		public TextAlignment ValueTextAlignment(bool right) => right ? TextAlignment.Right : TextAlignment.Left;

		/// <summary>A right-set value also takes the (empty) key column, so it reaches the row's true right edge.</summary>
		public int ValueSpan(bool right) => right ? 2 : 1;
	}
}
