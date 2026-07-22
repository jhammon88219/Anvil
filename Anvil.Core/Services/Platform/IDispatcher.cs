using System;

namespace Anvil.Services
{
	/// <summary>
	/// Marshals a callback onto the UI thread. The view-models refresh from background timers
	/// (<see cref="Anvil.ViewModels.BackgroundRefresh"/>) but raise
	/// <see cref="System.ComponentModel.INotifyPropertyChanged"/>, which WinUI requires on the UI
	/// thread — so they need a way to hop threads WITHOUT referencing WinUI, or Anvil.Core could not
	/// stay a plain net8.0 assembly. This is that seam; Anvil.App supplies the DispatcherQueue-backed
	/// implementation (WinUiDispatcher), exactly like <see cref="ILocationService"/> and
	/// <see cref="ISettingsService"/> do for their platform APIs.
	/// </summary>
	public interface IDispatcher
	{
		/// <summary>
		/// Queue <paramref name="action"/> to run on the UI thread. Fire-and-forget: like
		/// <c>DispatcherQueue.TryEnqueue</c>, a failure to enqueue (the queue is shutting down, i.e. the
		/// window is closing) is swallowed rather than thrown — callers are refresh handlers with
		/// nothing useful to do about it.
		/// </summary>
		void Post(Action action);
	}
}
