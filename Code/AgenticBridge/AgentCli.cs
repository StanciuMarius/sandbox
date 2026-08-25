using System;
using System.Threading.Tasks;

/// <summary>
/// Puts the agent CLI somewhere an agent can actually run it.
///
/// The script ships inside the package, but package content lives in s&box's
/// virtual filesystem - there is no stable path on disk to hand anyone. So we
/// copy it into this package's data folder, which is a real directory, and give
/// out that path instead.
///
/// The upshot is that a player installs nothing. Windows already has PowerShell,
/// the game already has the script, and the Q menu hands them the one line that
/// joins the two.
/// </summary>
internal static class AgentCli
{
	/// <summary>Where the script lives inside the package.</summary>
	private const string SourcePath = "agent/sbx.ps1";

	/// <summary>Where we copy it to, relative to this package's data folder.</summary>
	private const string InstalledPath = "agent/sbx.ps1";

	/// <summary>Full path to the extracted script, or null if it isn't there.</summary>
	public static string ScriptPath { get; private set; }

	/// <summary>
	/// The line a player pastes to let an agent drive their session.
	/// -ExecutionPolicy Bypass because the default policy blocks unsigned scripts,
	/// and asking players to change a machine-wide security setting is worse.
	/// </summary>
	public static string Command =>
		ScriptPath is null
			? "(the agent CLI could not be unpacked - see the console)"
			: $"powershell -ExecutionPolicy Bypass -File \"{ScriptPath}\"";

	/// <summary>
	/// Copy the script out of the package. Cheap, and rewriting every launch means
	/// a game update can't leave a stale script behind.
	/// </summary>
	public static void Install()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( SourcePath ) )
			{
				Log.Warning( $"[bridge] {SourcePath} is missing from the package - is it in the sbproj Resources list?" );
				return;
			}

			var contents = FileSystem.Mounted.ReadAllText( SourcePath );

			FileSystem.Data.CreateDirectory( "agent" );
			FileSystem.Data.WriteAllText( InstalledPath, contents );

			ScriptPath = FileSystem.Data.GetFullPath( InstalledPath );

			Log.Info( $"[bridge] agent CLI ready at {ScriptPath}" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bridge] couldn't unpack the agent CLI: {e.Message}" );
		}
	}

	[ConCmd( "bridge_cli", Help = "Print the command that lets an agent drive this session." )]
	public static void PrintCommand()
	{
		if ( ScriptPath is null )
			Install();

		Log.Info( $"[bridge] {Command}" );
	}
}
