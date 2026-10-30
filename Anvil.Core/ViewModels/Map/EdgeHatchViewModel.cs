using System.Collections.Generic;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// EDGE-HATCH (experimental bolt-on — grep EDGE-HATCH): the cross-hatch band where the chrome meets the map, in place
	/// of the black cast shadow. Settings → Edges binds the pickers + sliders; the bands (MainWindow's bottom one, each
	/// TemporalPane's map-side one) bind the read-outs. Every value is ONE AppSettings property (persisted, normalized on
	/// read), so there is no second copy to drift.
	/// </summary>
	public sealed class EdgeHatchViewModel : ObservableObject
	{
		private readonly AppSettings _settings;

		internal EdgeHatchViewModel(AppSettings settings) => _settings = settings;

		public IReadOnlyList<string> StyleLabels => EdgeHatchStyles.Labels;
		public IReadOnlyList<string> ColorLabels => EdgeHatchColors.Labels;

		/// <summary>Thinning / Fading / Both / Black shadow, as the picker's index (persisted as a token).</summary>
		public int StyleIndex
		{
			get => EdgeHatchStyles.IndexOf(_settings.EdgeHatchStyle);
			set
			{
				var token = EdgeHatchStyles.At(value);
				if (EdgeHatchStyles.Normalize(_settings.EdgeHatchStyle) == token) { return; }
				_settings.EdgeHatchStyle = token;
				RaiseStyle();
			}
		}

		/// <summary>A hatch draws (else the black cast shadow does, as before).</summary>
		public bool IsHatch => EdgeHatchStyles.IsHatch(_settings.EdgeHatchStyle);

		/// <summary>The hatch draws on the panes' map-side edges too.</summary>
		public bool IsPaneHatch => IsHatch && OnPanes;

		public bool Thins => EdgeHatchStyles.Thins(_settings.EdgeHatchStyle);
		public bool Fades => EdgeHatchStyles.Fades(_settings.EdgeHatchStyle);

		/// <summary>Bar surface / Black / Faint white, as the picker's index.</summary>
		public int ColorIndex
		{
			get => EdgeHatchColors.IndexOf(_settings.EdgeHatchColor);
			set
			{
				var token = EdgeHatchColors.At(value);
				if (EdgeHatchColors.Normalize(_settings.EdgeHatchColor) == token) { return; }
				_settings.EdgeHatchColor = token;
				OnPropertyChanged();
				OnPropertyChanged(nameof(ColorToken));
			}
		}

		public string ColorToken => EdgeHatchColors.Normalize(_settings.EdgeHatchColor);

		/// <summary>The band's depth into the map, px.</summary>
		public double Width
		{
			get => EdgeHatchSizes.Width(_settings.EdgeHatchWidth);
			set => SetSize(EdgeHatchSizes.Width(value), Width, v => _settings.EdgeHatchWidth = v);
		}

		/// <summary>The gap between lines along the edge, px.</summary>
		public double Spacing
		{
			get => EdgeHatchSizes.Spacing(_settings.EdgeHatchSpacing);
			set => SetSize(EdgeHatchSizes.Spacing(value), Spacing, v => _settings.EdgeHatchSpacing = v);
		}

		/// <summary>The line thickness, px (half-pixel steps).</summary>
		public double Weight
		{
			get => EdgeHatchSizes.Weight(_settings.EdgeHatchWeight);
			set => SetSize(EdgeHatchSizes.Weight(value), Weight, v => _settings.EdgeHatchWeight = v);
		}

		public bool OnPanes
		{
			get => _settings.EdgeHatchOnPanes;
			set
			{
				if (_settings.EdgeHatchOnPanes == value) { return; }
				_settings.EdgeHatchOnPanes = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsPaneHatch));
			}
		}

		public string WidthText => $"{Width:0} px";
		public string SpacingText => $"{Spacing:0} px";
		public string WeightText => $"{Weight:0.0} px";

		/// <summary>The tab's Restore defaults: the user's picks from the mock-up.</summary>
		public void RestoreDefaults()
		{
			StyleIndex = EdgeHatchStyles.IndexOf(EdgeHatchStyles.Both);
			ColorIndex = EdgeHatchColors.IndexOf(EdgeHatchColors.Surface);
			Width = EdgeHatchSizes.DefaultWidth;
			Spacing = EdgeHatchSizes.DefaultSpacing;
			Weight = EdgeHatchSizes.DefaultWeight;
			OnPanes = true;
		}

		private void RaiseStyle()
		{
			OnPropertyChanged(nameof(StyleIndex));
			OnPropertyChanged(nameof(IsHatch));
			OnPropertyChanged(nameof(IsPaneHatch));
			OnPropertyChanged(nameof(Thins));
			OnPropertyChanged(nameof(Fades));
		}

		private void SetSize(double value, double current, System.Action<double> write, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
		{
			if (value == current) { return; }
			write(value);
			OnPropertyChanged(name);
			OnPropertyChanged(name + "Text");
		}
	}
}
