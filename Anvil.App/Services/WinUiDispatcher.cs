using System;
using Microsoft.UI.Dispatching;

namespace Anvil.Services
{
	/// <summary>
	/// WinUI implementation of <see cref="IDispatcher"/> — wraps the UI thread's
	/// <see cref="DispatcherQueue"/>. Lives in Anvil.App because <c>Microsoft.UI.Dispatching</c> is a
	/// WinUI dependency and Anvil.Core is plain net8.0.
	/// </summary>
	/// <remarks>
	/// ⚠️ Must be constructed ON the UI thread — <see cref="DispatcherQueue.GetForCurrentThread"/>
	/// resolves the *calling* thread's queue, and returns null off it. MainWindow builds it during
	/// construction, which satisfies that. This is the same requirement the view-models had before the
	/// split (they called GetForCurrentThread in their own constructors); it has just moved to one place.
	/// </remarks>
	internal sealed class WinUiDispatcher : IDispatcher
	{
		private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();

		public void Post(Action action) => _queue.TryEnqueue(() => action());
	}
}
