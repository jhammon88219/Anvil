using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One row of the loading screen (see the XAML header): a big "22 / 28" count beside one cell per frame, each filled
	/// from the bottom by its fraction in <see cref="Cells"/>, and a caption line under the cells. The cell is declared in
	/// the XAML (CellTemplate); this keeps one <see cref="LoopLoadCell"/> per frame and updates their fractions in place.
	/// </summary>
	public sealed partial class LoopLoadProgressBar : UserControl
	{
		private List<LoopLoadCell> _cells = new();

		public LoopLoadProgressBar()
		{
			InitializeComponent();
			UpdateCells();
		}

		public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }
		public static readonly DependencyProperty CountProperty =
			DependencyProperty.Register(nameof(Count), typeof(int), typeof(LoopLoadProgressBar), new PropertyMetadata(0));

		public int Total { get => (int)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
		public static readonly DependencyProperty TotalProperty =
			DependencyProperty.Register(nameof(Total), typeof(int), typeof(LoopLoadProgressBar), new PropertyMetadata(0));

		/// <summary>The word under the count ("downloaded", "built").</summary>
		public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
		public static readonly DependencyProperty LabelProperty =
			DependencyProperty.Register(nameof(Label), typeof(string), typeof(LoopLoadProgressBar), new PropertyMetadata(string.Empty));

		/// <summary>Left of the caption line: the oldest frame still in progress.</summary>
		public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
		public static readonly DependencyProperty CaptionProperty =
			DependencyProperty.Register(nameof(Caption), typeof(string), typeof(LoopLoadProgressBar), new PropertyMetadata(string.Empty));

		/// <summary>Right of the caption line ("12 at once · 61 MB").</summary>
		public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
		public static readonly DependencyProperty DetailProperty =
			DependencyProperty.Register(nameof(Detail), typeof(string), typeof(LoopLoadProgressBar), new PropertyMetadata(string.Empty));

		/// <summary>Per frame, 0–1. A new list re-fills the cells; a different length re-lists them.</summary>
		public IReadOnlyList<double>? Cells { get => (IReadOnlyList<double>?)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
		public static readonly DependencyProperty CellsProperty =
			DependencyProperty.Register(nameof(Cells), typeof(IReadOnlyList<double>), typeof(LoopLoadProgressBar),
				new PropertyMetadata(null, (d, _) => ((LoopLoadProgressBar)d).UpdateCells()));

		/// <summary>The cells' colour — a DATA literal ("#FF9AA4B2"), never a theme brush (the gate is never themed).</summary>
		public Color Fill { get => (Color)GetValue(FillProperty); set => SetValue(FillProperty, value); }
		public static readonly DependencyProperty FillProperty =
			DependencyProperty.Register(nameof(Fill), typeof(Color), typeof(LoopLoadProgressBar),
				new PropertyMetadata(Color.FromArgb(0xFF, 0x9A, 0xA4, 0xB2), (d, e) => ((LoopLoadProgressBar)d).OnFillChanged((Color)e.NewValue)));

		public string CountText(int count) => count.ToString(System.Globalization.CultureInfo.CurrentCulture);
		// No leading space: the XAML's two Runs sit on separate lines, and that line break already renders as one.
		public string TotalText(int total) => total > 0 ? $"/ {total}" : "/ …";

		// The one shared brush is declared in the XAML; only its colour follows the DP.
		private void OnFillChanged(Color color) => ((SolidColorBrush)Resources["CellFillBrush"]).Color = color;

		private void UpdateCells()
		{
			var cells = Cells ?? Array.Empty<double>();
			var n = Math.Max(1, cells.Count); // no cells yet → one empty track across the row
			if (_cells.Count != n)
			{
				_cells = new List<LoopLoadCell>(n);
				for (var i = 0; i < n; i++) _cells.Add(new LoopLoadCell());
				CellsHost.ItemsSource = _cells;
			}
			for (var i = 0; i < n; i++)
			{
				_cells[i].Fraction = i < cells.Count ? cells[i] : 0;
			}
		}
	}
}

namespace Anvil.Controls.Primitives
{
	/// <summary>One frame's cell in a <see cref="LoopLoadProgressBar"/>: how far that frame has got (0–1), as the
	/// height and brightness its declared template draws.</summary>
	public sealed class LoopLoadCell : INotifyPropertyChanged
	{
		private const double CellHeight = 30;          // the cells' row height (LoopLoadProgressBar.xaml)
		private const double InProgressOpacity = 0.6;  // a part-done frame reads dimmer than a done one
		private double _fraction;

		public double Fraction
		{
			get => _fraction;
			set
			{
				var v = Math.Clamp(value, 0, 1);
				if (v == _fraction) return; // only a changed cell re-lays out
				_fraction = v;
				OnPropertyChanged(nameof(FillHeight));
				OnPropertyChanged(nameof(FillOpacity));
			}
		}

		public double FillHeight => _fraction * CellHeight;
		public double FillOpacity => _fraction >= 1 ? 1 : InProgressOpacity;

		public event PropertyChangedEventHandler? PropertyChanged;
		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
