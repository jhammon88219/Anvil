using System;
using System.Linq;
using Windows.Security.Credentials;

namespace Anvil.Services
{
	/// <summary>
	/// <see cref="ICredentialStore"/> over Windows Credential Manager (<see cref="PasswordVault"/>, the user's own vault —
	/// visible under Control Panel → Credential Manager → Web Credentials). Holds the NOAA Weather Wire password only.
	/// </summary>
	/// <remarks>⚠️ <see cref="PasswordVault"/> throws for a resource it doesn't hold — every call swallows that into
	/// "nothing saved", as the interface promises.</remarks>
	public sealed class CredentialStore : ICredentialStore
	{
		public string? Read(string resource, string user)
		{
			try
			{
				var credential = new PasswordVault().Retrieve(resource, user);
				credential.RetrievePassword();
				return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
			}
			catch (Exception)
			{
				return null;
			}
		}

		public bool Write(string resource, string user, string password)
		{
			try
			{
				new PasswordVault().Add(new PasswordCredential(resource, user, password));
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public void Delete(string resource)
		{
			try
			{
				var vault = new PasswordVault();
				foreach (var credential in vault.FindAllByResource(resource).ToList()) { vault.Remove(credential); }
			}
			catch (Exception)
			{
				// Nothing saved under it — nothing to forget.
			}
		}
	}
}
