using System;
using System.Collections.Generic;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One map pane: a PRODUCT VIEW of the shared loop. Panes share the site, the camera, the time cursor
	/// and the tilt — a pane differs from its siblings only in which moment it draws — so this view model
	/// is deliberately small. Everything about the loop itself stays on <see cref="RadarViewModel"/>.
	///
	/// <para>Four are always constructed; <see cref="IsVisible"/> follows the layout. Keeping the hidden
	/// ones alive means a pane remembers its product across a trip through single-pane, and it lets the
	/// notches bind to fixed properties (Pane0…Pane3) instead of an index into a mutating list.</para>
	///
	/// <para>The pane's NOTCH binds straight to this - the small hideable island of chrome at the top of
	/// that pane (App: Composites/PaneNotchContent). It holds the product selector, the colour-ramp legend
	/// and, for now, the shared tilt. The selector and the legend used to be ONE control, a chip in the
	/// bottom bar; they came apart when the notch gave them room.</para>
	/// </summary>
	public sealed class RadarPaneViewModel : ObservableObject
	{
		private readonly Action<RadarPaneViewModel> _onProductChanged;

		public RadarPaneViewModel(
			int index,
			IReadOnlyList<RadarProductOption> productOptions,
			Action<RadarPaneViewModel> onProductChanged)
		{
			Index = index;
			ProductOptions = productOptions;
			_onProductChanged = onProductChanged;
		}

		/// <summary>Which pane this is. 0 is the MAIN pane (bottom-left in a quad); it is the view that
		/// was already on screen before a multi-pane layout was entered, so it is never reassigned.</summary>
		public int Index { get; }

		/// <summary>
		/// The selectable products — the SAME <see cref="RadarProductOption"/> instances every pane binds
		/// to. Sharing them is deliberate and safe: an option carries only static facts plus the colour
		/// ramp pushed from the WebView, and nothing per-pane. Only the selected INDEX is per-pane.
		/// </summary>
		public IReadOnlyList<RadarProductOption> ProductOptions { get; }

		private bool _isVisible;

		/// <summary>Whether the current layout shows this pane. Drives its notch's visibility.</summary>
		public bool IsVisible
		{
			get => _isVisible;
			set => SetProperty(ref _isVisible, value);
		}

		private int _productIndex;

		/// <summary>Index into <see cref="ProductOptions"/> — what this pane draws. Two-way with the notch's product selector.</summary>
		public int ProductIndex
		{
			get => _productIndex;
			set
			{
				if (value < 0 || value >= ProductOptions.Count || !SetProperty(ref _productIndex, value))
				{
					return;
				}

				RaiseProductDerived();
				_onProductChanged(this);
			}
		}

		/// <summary>The selected option, or null if the index is somehow out of range.</summary>
		public RadarProductOption? SelectedProduct =>
			_productIndex >= 0 && _productIndex < ProductOptions.Count ? ProductOptions[_productIndex] : null;

		/// <summary>The JS product id (radar-products.js) this pane renders.</summary>
		public string ProductId => SelectedProduct?.Id ?? "reflectivity";

		/// <summary>Short label ("Ref"/"Vel"/…) shown in this pane's notch.</summary>
		public string ShortLabel => SelectedProduct?.ShortLabel ?? string.Empty;

		/// <summary>
		/// This pane's colour ramp. Resolved LOCALLY from the selected option rather than from a per-pane
		/// push out of the WebView: the full ramp table already arrives once via
		/// <c>RadarViewModel.SetAllRamps</c>, which is what makes four independent inspect ticks nearly free.
		/// Null until that table lands.
		/// </summary>
		public RadarRampInfo? Ramp => SelectedProduct?.Ramp;

		/// <summary>Whether this pane is on reflectivity — the one product that is always built.</summary>
		public bool IsReflectivity => ProductId == "reflectivity";

		/// <summary>Whether this pane shows a Doppler product (velocity or SRV), which needs the loop's
		/// VAD storm motion.</summary>
		public bool IsDoppler => ProductId is "velocity" or "srv";

		// ── Inspect ────────────────────────────────────────────────────────────────────────────────────
		// Inspect is a GLOBAL instrument — one armed cursor over the map — but the VALUE is per pane: at
		// one lat/lon each pane reads its own product's grid, so four panes give four readings of the same
		// point, each ticking on its own notch ramp.
		private bool _hasInspectValue;
		private double _inspectFraction;

		/// <summary>Position (0-1) of the inspected value along this pane's ramp.</summary>
		/// <remarks>
		/// ⚠️ A POSITION, not a number — the notch draws a tick on the ramp and nothing more. The formatted
		/// value ("47.5 dBZ", speeds in m/s + mph) is drawn by the WebView beside the cursor, so a host-side
		/// InspectValueText re-formatted it per pane on every pointer move for no reader; it is deleted.
		/// Putting the number in the notch means that string back, and its raise below.
		/// </remarks>
		public double InspectFraction => _inspectFraction;

		/// <summary>Whether the live inspect tick should be drawn on this pane's notch ramp.</summary>
		public bool IsInspectMarkerVisible => _isInspecting && _hasInspectValue && Ramp is not null;

		private bool _isInspecting;

		/// <summary>Set by the owner when Inspect is armed/disarmed (it is one mode for the whole map).</summary>
		public void SetInspecting(bool on)
		{
			if (_isInspecting == on)
			{
				return;
			}

			_isInspecting = on;
			if (!on)
			{
				SetInspectValue(null);
			}

			OnPropertyChanged(nameof(IsInspectMarkerVisible));
		}

		/// <summary>Called when the WebView reports the value under the cursor FOR THIS PANE (null = none).</summary>
		public void SetInspectValue(double? value)
		{
			if (value is double v && Ramp is { } r)
			{
				var span = r.Max - r.Min;
				if (span <= 0)
				{
					span = 1;
				}

				_inspectFraction = Math.Clamp((v - r.Min) / span, 0, 1);
				_hasInspectValue = true;
			}
			else
			{
				_hasInspectValue = false;
			}

			OnPropertyChanged(nameof(InspectFraction));
			OnPropertyChanged(nameof(IsInspectMarkerVisible));
		}

		/// <summary>
		/// Re-raise everything derived from the selected product. Called on a product change and again when
		/// the WebView's ramp table lands (an option's <see cref="RadarProductOption.Ramp"/> is filled late,
		/// so a pane's ramp-dependent state has to be re-announced then).
		/// </summary>
		public void RaiseProductDerived()
		{
			OnPropertyChanged(nameof(SelectedProduct));
			OnPropertyChanged(nameof(ProductId));
			OnPropertyChanged(nameof(ShortLabel));
			OnPropertyChanged(nameof(Ramp));
			OnPropertyChanged(nameof(IsReflectivity));
			OnPropertyChanged(nameof(IsDoppler));
			OnPropertyChanged(nameof(IsInspectMarkerVisible));
		}

		/// <summary>Set the product WITHOUT notifying the owner — used when the owner is itself assigning a
		/// layout's default products and will drive the WebView push in its own order.</summary>
		public void SetProductIndexSilently(int index)
		{
			if (index < 0 || index >= ProductOptions.Count || index == _productIndex)
			{
				return;
			}

			_productIndex = index;
			OnPropertyChanged(nameof(ProductIndex));
			RaiseProductDerived();
		}
	}
}
