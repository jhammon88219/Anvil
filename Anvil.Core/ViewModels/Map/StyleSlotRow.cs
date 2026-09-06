using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One editable colour in the basemap style — a row in the dev style editor.
	/// </summary>
	/// <remarks>
	/// ⚠️ A SLOT IS (layer, paint property, OCCURRENCE INDEX), and each part is load-bearing:
	/// <list type="bullet">
	/// <item>not just a LAYER, because one paint property can hold several colours (Data Viz Light's
	/// <c>landuse_park</c> holds seven inside a <c>['case', …]</c> expression);</item>
	/// <item>not a distinct COLOUR, because <c>#ffffff</c> is the earth fill AND twelve road casings AND
	/// eight label halos — editing by colour moves three unrelated things together, which is precisely
	/// what "full control" is not.</item>
	/// </list>
	/// <see cref="Key"/> is the wire format the page keys its override table by; it is built here so the
	/// two sides cannot disagree about the separator.
	/// </remarks>
	public sealed class StyleSlotRow : ObservableObject
	{
		public StyleSlotRow(string key, string layerId, string property, int index, string baseColor, string? overrideColor)
		{
			Key = key;
			LayerId = layerId;
			Property = property;
			Index = index;
			BaseColor = baseColor;
			_overrideColor = overrideColor;
		}

		/// <summary>The page's override-table key: <c>layer|property|index</c>.</summary>
		public string Key { get; }

		public string LayerId { get; }
		public string Property { get; }

		/// <summary>Which colour within the property — 0 unless it holds an expression with several.</summary>
		public int Index { get; }

		/// <summary>The colour as the style FILE has it, before the transform or any override.</summary>
		public string BaseColor { get; }

		/// <summary>
		/// The layer-id family this row groups under (<c>roads</c>, <c>water</c>, <c>landuse</c>, …).
		/// ⚠️ Derived from the id's first segment rather than stored: the Protomaps schema names layers
		/// consistently across all five bundled styles, so the grouping needs no per-style table.
		/// </summary>
		public string Family
		{
			get
			{
				var cut = LayerId.IndexOf('_');
				return cut > 0 ? LayerId[..cut] : LayerId;
			}
		}

		/// <summary>What the row shows as its label: the property, plus the index only when it disambiguates.</summary>
		public string Label => Index == 0 ? $"{LayerId} · {Property}" : $"{LayerId} · {Property} [{Index}]";

		private string? _overrideColor;
		/// <summary>The explicit colour for this slot, or null to let the transform decide.</summary>
		public string? OverrideColor
		{
			get => _overrideColor;
			private set
			{
				if (SetProperty(ref _overrideColor, value))
				{
					OnPropertyChanged(nameof(IsOverridden));
					OnPropertyChanged(nameof(EffectiveColor));
				}
			}
		}

		/// <summary>Whether this slot carries an explicit colour rather than following the transform.</summary>
		public bool IsOverridden => !string.IsNullOrWhiteSpace(_overrideColor);

		/// <summary>
		/// What the swatch shows. ⚠️ The BASE colour when there is no override — NOT the transformed one.
		/// The editor deliberately does not mirror the transform per row: the map is the preview, and a
		/// second, slightly-stale copy of the maths in the list is exactly the drift this design avoids.
		/// </summary>
		public string EffectiveColor => _overrideColor ?? BaseColor;

		/// <summary>Sets or clears this row's explicit colour. Called by the view model, which owns the table.</summary>
		public void ApplyOverride(string? hex) => OverrideColor = hex;
	}
}
