using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Converters
{
	/// <summary>
	/// Converts a "#RRGGBB" (or "#AARRGGBB") hex string to a <see cref="SolidColorBrush"/>. Malformed input
	/// degrades to transparent (never throws).
	/// </summary>
	/// <remarks>
	/// TWO consumers, and they are unrelated: the SPC outlook legend swatches (whose colors come straight
	/// from the outlook GeoJSON's official SPC <c>fill</c>/<c>stroke</c> values - data), and the dev style
	/// editor's colour swatches (basemap chrome being edited by hand). Both just need hex to brush.
	/// ⚠️ A CONVERTER rather than an x:Bind function because both call sites are inside a
	/// <c>DataTemplate</c>, where a function path resolves against the template's <c>x:DataType</c> rather
	/// than the page - and the style editor's row type lives in Anvil.Core, which cannot return a WinUI
	/// <c>Brush</c> at all. ⚠️ The XAML compiler reports that mistake as a generic internal error
	/// ("Could not find any resources appropriate for the specified culture"), so it is worth recognising
	/// rather than chasing elsewhere.
	/// </remarks>
	public sealed class HexToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language) =>
			new SolidColorBrush(ColorUtil.FromHex(value as string));

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
