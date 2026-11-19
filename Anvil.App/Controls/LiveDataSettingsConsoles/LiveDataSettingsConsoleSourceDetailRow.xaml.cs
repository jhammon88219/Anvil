using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>One row of the Source plate's details table: a trait and its value per source, lifted on hover (see the XAML header).</summary>
	public sealed partial class LiveDataSettingsConsoleSourceDetailRow : UserControl
	{
		public LiveDataSettingsConsoleSourceDetailRow()
		{
			InitializeComponent();
		}

		/// <summary>The trait, in the label column ("How fast").</summary>
		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}

		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>The IEM live feed's value.</summary>
		public string Iem
		{
			get => (string)GetValue(IemProperty);
			set => SetValue(IemProperty, value);
		}

		public static readonly DependencyProperty IemProperty =
			DependencyProperty.Register(nameof(Iem), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>NOAA Weather Wire's value.</summary>
		public string Nwws
		{
			get => (string)GetValue(NwwsProperty);
			set => SetValue(NwwsProperty, value);
		}

		public static readonly DependencyProperty NwwsProperty =
			DependencyProperty.Register(nameof(Nwws), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>Polling only's value.</summary>
		public string Polling
		{
			get => (string)GetValue(PollingProperty);
			set => SetValue(PollingProperty, value);
		}

		public static readonly DependencyProperty PollingProperty =
			DependencyProperty.Register(nameof(Polling), typeof(string), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(""));

		/// <summary>The picked source (0 IEM · 1 Weather Wire · 2 Polling only) — the other two columns grey back.</summary>
		public int PickedIndex
		{
			get => (int)GetValue(PickedIndexProperty);
			set => SetValue(PickedIndexProperty, value);
		}

		public static readonly DependencyProperty PickedIndexProperty =
			DependencyProperty.Register(nameof(PickedIndex), typeof(int), typeof(LiveDataSettingsConsoleSourceDetailRow), new PropertyMetadata(0));

		/// <summary>A cell's opacity: full for the picked source, <see cref="UnpickedOpacity"/> for the others.</summary>
		public double CellOpacity(int picked, int column) => picked == column ? 1.0 : UnpickedOpacity;

		/// <summary>How far a source you didn't pick greys back (0.45, the activity band's idle dim).</summary>
		public const double UnpickedOpacity = 0.45;

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "PointerOver", false);

		private void OnPointerLeft(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Normal", false);
	}
}
