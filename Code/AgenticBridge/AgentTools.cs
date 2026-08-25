using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Finds the local player's toolgun and gets a named tool ready to use.
///
/// Agents name tools the way people do - "weld", "wheel", "rope" - so names are derived from the
/// type name with and without its Tool suffix, rather than from each tool's ClassName, which are
/// not consistent with one another across the codebase ("weld" but "wheeltool").
/// </summary>
internal static class AgentTools
{
	/// <summary>
	/// Name to tool type, built once from the type library so a tool added later shows up here for
	/// free rather than needing a line in a list somebody forgets to update.
	/// </summary>
	private static Dictionary<string, TypeDescription> _byName;

	private static Dictionary<string, TypeDescription> ByName
	{
		get
		{
			if ( _byName is not null )
				return _byName;

			_byName = new Dictionary<string, TypeDescription>( StringComparer.OrdinalIgnoreCase );

			foreach ( var type in Game.TypeLibrary.GetTypes<ToolMode>() )
			{
				if ( type.IsAbstract ) continue;

				// "WheelTool" -> wheeltool, wheel
				Register( type.TargetType.Name, type );

				if ( type.TargetType.Name.EndsWith( "Tool", StringComparison.OrdinalIgnoreCase ) )
					Register( type.TargetType.Name[..^4], type );
			}

			// what people actually call a few of these
			Alias( "upright", "KeepUpright" );
			Alias( "link", "Linker" );
			Alias( "resize", "Resizer" );
			Alias( "remove", "Remover" );

			return _byName;
		}
	}

	private static void Register( string name, TypeDescription type )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return;

		// first registration wins, so the plain "wheel" form isn't stolen by a later tool
		_byName.TryAdd( name.Trim(), type );
	}

	/// <summary>Point a friendlier name at a tool that's already registered.</summary>
	private static void Alias( string alias, string existing )
	{
		if ( _byName.TryGetValue( existing, out var type ) )
			_byName.TryAdd( alias, type );
	}

	/// <summary>
	/// Every tool once, under the shortest name it answers to - what an agent should be shown,
	/// rather than each of its aliases. Lowercased to match how they get written in practice;
	/// lookups are case-insensitive either way.
	/// </summary>
	public static IEnumerable<(string Name, TypeDescription Type)> Distinct =>
		ByName.GroupBy( x => x.Value.TargetType )
			.Select( g => (Name: g.OrderBy( x => x.Key.Length ).First().Key.ToLowerInvariant(), Type: g.First().Value) )
			.OrderBy( x => x.Name );

	/// <summary>The local player's toolgun, whether or not they're holding it.</summary>
	public static Toolgun Toolgun
	{
		get
		{
			var player = Player.FindLocalPlayer();
			if ( !player.IsValid() )
				throw new InvalidOperationException( "No local player - is the game in a session rather than the main menu?" );

			var toolgun = player.GetComponentInChildren<Toolgun>( true );
			if ( !toolgun.IsValid() )
				throw new InvalidOperationException( "The player has no toolgun" );

			return toolgun;
		}
	}

	/// <summary>
	/// Resolve a tool by name and make it the active mode.
	/// </summary>
	/// <remarks>
	/// Switching for real, rather than reaching into a disabled component, is deliberate: it keeps
	/// the tool's own state and RPCs on the path they were written for, and it shows the player on
	/// the toolgun screen which tool the agent is reaching for.
	/// </remarks>
	public static ToolMode Activate( string name ) => Toolgun.ActivateMode( Get( name ) );

	/// <summary>
	/// Resolve a tool by name without making it active.
	/// </summary>
	/// <remarks>
	/// For reading a tool's settings, or borrowing its trace to resolve a target - neither of which
	/// should yank the player's held tool out from under them.
	/// </remarks>
	public static ToolMode Get( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( $"No tool named. Known tools: {string.Join( ", ", Distinct.Select( x => x.Name ) )}" );

		if ( !ByName.TryGetValue( name.Trim(), out var type ) )
			throw new ArgumentException( $"Unknown tool '{name}'. Known tools: {string.Join( ", ", Distinct.Select( x => x.Name ) )}" );

		var mode = Toolgun.GetComponents<ToolMode>( true )
			.FirstOrDefault( x => x.GetType() == type.TargetType );

		if ( !mode.IsValid() )
			throw new InvalidOperationException( $"The toolgun has no '{name}' component - it may not have finished setting up yet." );

		return mode;
	}

	/// <summary>
	/// Resolve and activate a tool, insisting it be of a particular kind.
	/// </summary>
	public static T Activate<T>( string name ) where T : ToolMode
	{
		var mode = Get( name );

		// check before switching - naming the wrong tool shouldn't leave the player holding it
		if ( mode is not T typed )
			throw new ArgumentException( $"'{name}' is a {mode.GetType().Name}, which doesn't do that." );

		Toolgun.ActivateMode( typed );

		return typed;
	}

	/// <summary>
	/// The tool's editable settings - the same [Property] values the player sees in the tool panel,
	/// which is how an agent changes rope slack or which thruster model to place.
	/// </summary>
	public static IEnumerable<PropertyDescription> Options( ToolMode tool )
	{
		return tool.TypeDescription.Properties
			.Where( x => x.HasAttribute<PropertyAttribute>() )
			.OrderBy( x => x.Name );
	}
}
