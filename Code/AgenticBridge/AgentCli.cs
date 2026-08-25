using System;

/// <summary>
/// Puts the agent CLI, and instructions for using it, somewhere an agent can
/// actually reach.
///
/// Both ship inside the package, but package content lives in s&box's virtual
/// filesystem - there is no stable path on disk to hand anyone. So we copy them
/// into this package's data folder, which is a real directory, and give out that
/// path instead.
///
/// The player hands their agent one line pointing at the README. The agent reads
/// it, learns the command and the conventions, and gets on with it - so the
/// player never has to explain the game.
/// </summary>
internal static class AgentCli
{
	private const string ScriptSource = "agent/sbx.ps1";
	private const string ReadmeSource = "agent/README.md";

	private const string ScriptInstalled = "agent/sbx.ps1";
	private const string ReadmeInstalled = "agent/README.md";

	/// <summary>Replaced in the README with the real invocation for this machine.</summary>
	private const string CommandToken = "{{SBX}}";

	/// <summary>Full path to the extracted script, or null if it isn't there.</summary>
	public static string ScriptPath { get; private set; }

	/// <summary>Full path to the extracted instructions, or null if they aren't there.</summary>
	public static string ReadmePath { get; private set; }

	/// <summary>
	/// How to invoke the CLI on this machine. -ExecutionPolicy Bypass because the
	/// default policy blocks unsigned scripts, and asking players to change a
	/// machine-wide security setting would be a worse answer.
	/// </summary>
	public static string ScriptCommand =>
		ScriptPath is null ? null : $"powershell -ExecutionPolicy Bypass -File \"{ScriptPath}\"";

	/// <summary>
	/// What the player pastes to their agent. A sentence rather than a command,
	/// because the useful thing is for the agent to go and read the instructions.
	/// </summary>
	public static string Prompt =>
		ReadmePath is null
			? "(the agent files could not be unpacked - see the console)"
			: $"Read \"{ReadmePath}\" and use it to control my Sandbox game session.";

	/// <summary>
	/// Copy both files out of the package, rewriting the README so its examples
	/// carry the real path. Cheap, and doing it every launch means a game update
	/// can't leave a stale copy behind.
	/// </summary>
	public static void Install()
	{
		ScriptPath = null;
		ReadmePath = null;

		try
		{
			if ( !FileSystem.Mounted.FileExists( ScriptSource ) || !FileSystem.Mounted.FileExists( ReadmeSource ) )
			{
				Log.Warning( $"[bridge] {ScriptSource} or {ReadmeSource} is missing from the package - are they in the sbproj Resources list?" );
				return;
			}

			FileSystem.Data.CreateDirectory( "agent" );

			FileSystem.Data.WriteAllText( ScriptInstalled, FileSystem.Mounted.ReadAllText( ScriptSource ) );
			ScriptPath = FileSystem.Data.GetFullPath( ScriptInstalled );

			// the README is templated, so it can only be written once we know where
			// the script landed
			var readme = FileSystem.Mounted.ReadAllText( ReadmeSource ).Replace( CommandToken, ScriptCommand );

			FileSystem.Data.WriteAllText( ReadmeInstalled, readme );
			ReadmePath = FileSystem.Data.GetFullPath( ReadmeInstalled );

			Log.Info( $"[bridge] agent instructions ready at {ReadmePath}" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bridge] couldn't unpack the agent files: {e.Message}" );
		}
	}

	[ConCmd( "bridge_cli", Help = "Print what to give an agent so it can drive this session." )]
	public static void PrintPrompt()
	{
		if ( ReadmePath is null )
			Install();

		Log.Info( $"[bridge] {Prompt}" );
	}
}
