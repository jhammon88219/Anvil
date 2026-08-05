using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Anvil.Converters
{
	/// <summary>
	/// Maps a bool to <see cref="Visibility"/> (true ⇒ Visible, false ⇒ Collapsed). Pass the string
	/// parameter "invert" to flip the mapping. Used for DataTemplate visibility where an x:Bind function
	/// isn't available (loose <see cref="ResourceDictionary"/> / item templates).
	/// </summary>
	public sealed class BoolToVisibilityConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			var flag = value is bool b && b;
			if (parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase))
			{
				flag = !flag;
			}
			return flag ? Visibility.Visible : Visibility.Collapsed;
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
