using System;
using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The PastCast saved-event list: the curated BUILT-IN events bundled into Anvil.Core, merged with the
	/// user's own, which persist to a JSON file under <c>%LocalAppData%\Anvil</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ Built-in events are READ-ONLY here, not merely in the UI: <see cref="Remove"/> refuses one and
	/// returns false. The list a user can delete from is only ever their own.
	/// </remarks>
	public interface ISavedEventLibrary
	{
		/// <summary>Every event, the user's first, then built-ins; each group newest first.</summary>
		IReadOnlyList<SavedEvent> GetEvents();

		/// <summary>
		/// Saves a new user event and returns it.
		/// </summary>
		/// <exception cref="ArgumentException">The name is blank, or a leg fails validation (see
		/// <see cref="SavedEventLibrary.Validate"/>) — the message is written for the user.</exception>
		SavedEvent Add(string name, IReadOnlyList<SavedEventLeg> legs, string notes);

		/// <summary>Deletes a USER event. False when there is no such event or it is built in.</summary>
		bool Remove(string id);
	}
}
