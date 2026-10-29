using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace Anvil.Controls.TemporalPanes.DragMoveLayerExpanders
{
	/// <summary>
	/// One toggle row of a layer expander's body — box, map mark (the content), name, count at the far right, hover lift.
	/// See the XAML header.
	/// </summary>
	[ContentProperty(Name = nameof(Marker))]
	public sealed partial class DragMoveLayerExpanderCountRow : UserControl
	{
		public DragMoveLayerExpanderCountRow()
		{
			InitializeComponent();
		}

		/// <summary>Whether the type is drawn (two-way with the VM's Show… flag).</summary>
		public bool IsChecked
		{
			get => (bool)GetValue(IsCheckedProperty);
			set => SetValue(IsCheckedProperty, value);
		}

		public static readonly DependencyProperty IsCheckedProperty =
			DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(DragMoveLayerExpanderCountRow), new PropertyMetadata(false));

		/// <summary>The map's mark for this type, declared by the host (a swatch Border, a Path…).</summary>
		public object? Marker
		{
			get => GetValue(MarkerProperty);
			set => SetValue(MarkerProperty, value);
		}

		public static readonly DependencyProperty MarkerProperty =
			DependencyProperty.Register(nameof(Marker), typeof(object), typeof(DragMoveLayerExpanderCountRow), new PropertyMetadata(null));

		/// <summary>The type's name ("Severe thunderstorm").</summary>
		public string Label
		{
			get => (string)GetValue(LabelProperty);
			set => SetValue(LabelProperty, value);
		}

		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(DragMoveLayerExpanderCountRow), new PropertyMetadata(""));

		/// <summary>The count, already formatted.</summary>
		public string Count
		{
			get => (string)GetValue(CountProperty);
			set => SetValue(CountProperty, value);
		}

		public static readonly DependencyProperty CountProperty =
			DependencyProperty.Register(nameof(Count), typeof(string), typeof(DragMoveLayerExpanderCountRow), new PropertyMetadata(""));

		// A disabled row's overlay dims as the CheckBox's own content would.
		public double Dim(bool enabled) => enabled ? 1.0 : 0.4;

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e) =>
			VisualStateManager.GoToState(this, "PointerOver", true);

		private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
			VisualStateManager.GoToState(this, "Normal", true);
	}
}
