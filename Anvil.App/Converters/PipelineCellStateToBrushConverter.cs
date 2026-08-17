// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit).
using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Anvil.ViewModels;

namespace Anvil.Converters
{
	/// <summary>
	/// Colors one Pipeline Console scrubber cell by its <see cref="PipelineCellState"/>: built-with-data
	/// (green), built-but-no-data (faint), in-flight / queued (amber, bright vs dim), and unbuilt (a barely
	/// there track). Deliberately theme-agnostic — the cells sit on the window's opaque surface.
	/// </summary>
	public sealed class PipelineCellStateToBrushConverter : IValueConverter
	{
		private static readonly Brush Built = ColorUtil.Solid(0xFF, 0x3F, 0xB9, 0x50);   // green — geometry ready
		private static readonly Brush NoData = ColorUtil.Solid(0x55, 0x8A, 0x8A, 0x8A);  // faint — built, no data
		private static readonly Brush InFlight = ColorUtil.Solid(0xFF, 0xE3, 0xB3, 0x41); // amber — decoding now
		private static readonly Brush Queued = ColorUtil.Solid(0x88, 0xE3, 0xB3, 0x41);  // dim amber — queued
		private static readonly Brush Unbuilt = ColorUtil.Solid(0x22, 0x80, 0x80, 0x80); // ghost — not built/queued

		public object Convert(object value, Type targetType, object parameter, string language) =>
			value is PipelineCellState state
				? state switch
				{
					PipelineCellState.Built => Built,
					PipelineCellState.NoData => NoData,
					PipelineCellState.InFlight => InFlight,
					PipelineCellState.Queued => Queued,
					_ => Unbuilt,
				}
				: Unbuilt;

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
