using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls
{
	/// <summary>
	/// A ComboBox that shows the hand ("I'm clickable") cursor on hover.
	///
	/// It exists as a subclass ONLY because of where WinUI keeps the cursor: <c>UIElement.ProtectedCursor</c>
	/// is protected, so it can't be reached from a Style or the host — it has to be set from inside the
	/// control's own class. It therefore adds NOTHING but the cursor; the look comes from whatever style the
	/// call site applies.
	///
	/// ⚠️ TRANSITIONAL — its last consumer is the radar Tilt combo. The Product combo used to share it, and
	/// now sets its own cursor as <see cref="ProductColorRampComboBox"/>; this file goes away once Tilt gets
	/// the same treatment (see the Controls standardization pass — every control becomes a UserControl).
	/// </summary>
	public partial class HandCursorComboBox : ComboBox
	{
		public HandCursorComboBox()
		{
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
		}
	}
}
