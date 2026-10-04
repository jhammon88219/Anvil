using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One row of the loading screen (see the XAML header): a big "22 / 28" count beside one cell per frame, each filled
	/// from the bottom by its fraction in <see cref="Cells"/>, and a caption line under the cells.
	/// </summary>
	public sealed partial class LoopLoadProgressBar : UserControl
	{
		private const double CellHeight = 30;
		private const double InProgressOpacity = 0.6;
		private static readonly Color Track = Color.FromArgb(0x1A, 0xE8, 0xEA, 0xED);

		private readonly List<Border> _fills = new();
		private SolidColorBrush _fillBrush = new(Color.FromArgb(0xFF, 0x9A, 0xA4, 0xB2));

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

		/// <summary>Per frame, 0–1. A new list re-fills the cells; a different length rebuilds them.</summary>
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

		private void OnFillChanged(Color color)
		{
			_fillBrush = new SolidColorBrush(color);
			foreach (var fill in _fills) fill.Background = _fillBrush;
		}

		private void UpdateCells()
		{
			var cells = Cells ?? Array.Empty<double>();
			var n = Math.Max(1, cells.Count); // no cells yet → one empty track across the row
			if (_fills.Count != n) Rebuild(n);
			for (var i = 0; i < n; i++)
			{
				var v = i < cells.Count ? Math.Clamp(cells[i], 0, 1) : 0;
				var fill = _fills[i];
				fill.Height = v * CellHeight;
				fill.Opacity = v >= 1 ? 1 : InProgressOpacity;
			}
		}

		private void Rebuild(int n)
		{
			CellsGrid.Children.Clear();
			CellsGrid.ColumnDefinitions.Clear();
			_fills.Clear();
			for (var i = 0; i < n; i++)
			{
				CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				var fill = new Border { VerticalAlignment = VerticalAlignment.Bottom, Background = _fillBrush, Height = 0 };
				var cell = new Grid { Background = new SolidColorBrush(Track), CornerRadius = new CornerRadius(2) };
				cell.Children.Add(fill);
				Grid.SetColumn(cell, i);
				CellsGrid.Children.Add(cell);
				_fills.Add(fill);
			}
		}
	}
}
