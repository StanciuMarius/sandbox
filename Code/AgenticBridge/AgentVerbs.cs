using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

/// <summary>
/// The operations an agent is allowed to perform, and nothing else.
///
/// Every verb routes through a command or RPC the game already exposes, so the
/// existing rules still hold: the host stays authoritative, props are attributed
/// to the caller, prop limits apply, and spawns land on the undo stack.
///
/// Adding a verb is the deliberate act of widening what an agent can do. Prefer
/// a narrow verb over a general one.
/// </summary>
internal static class AgentVerbs
{
	internal sealed class Verb
	{
		public string Name { get; init; }
		public string Description { get; init; }

		/// <summary>Argument name to human description, used to build the agent's tool schema.</summary>
		public Dictionary<string, string> Args { get; init; } = new();

		public Func<JsonObject, Task<JsonNode>> Handler { get; init; }
	}

	public static IReadOnlyDictionary<string, Verb> All => _verbs;

	private static readonly Dictionary<string, Verb> _verbs = new( StringComparer.OrdinalIgnoreCase );

	static AgentVerbs()
	{
		Add( new Verb
		{
			Name = "spawn_prop",
			Description = "Spawn a prop where the calling player is looking, exactly like the 'spawn' console command. " +
				"Traces from the player's eyes and places the prop on the first surface hit.",
			Args =
			{
				["ident"] = "What to spawn. Either a bare model path like 'models/dev/box.vmdl', or a prefixed " +
					"ident like 'prop:models/dev/box.vmdl', 'entity:<path>' or 'dupe.local:<id>'."
			},
			Handler = SpawnProp
		} );

		Add( new Verb
		{
			Name = "undo",
			Description = "Undo the calling player's most recent spawn or tool action.",
			Handler = Undo
		} );

		Add( new Verb
		{
			Name = "cleanup",
			Description = "Remove spawned objects. Cleaning up everything requires the admin permission on the host.",
			Args = { ["scope"] = "'mine' (default) removes only the caller's objects, 'all' removes everyone's." },
			Handler = Cleanup
		} );

		Add( new Verb
		{
			Name = "list_props",
			Description = "List spawned objects currently in the world, with their owner, model and position.",
			Args =
			{
				["owner"] = "Optional player name to filter by. Partial matches allowed.",
				["limit"] = "Maximum number to return. Default 100."
			},
			Handler = ListProps
		} );

		Add( new Verb
		{
			Name = "trace",
			Description = "Trace a ray through the world and report what it hits. With no arguments, traces from " +
				"the calling player's eyes along their view direction - what they are currently pointing at.",
			Args =
			{
				["from"] = "Optional start position as 'x,y,z'. Defaults to the player's eye position.",
				["to"] = "Optional end position as 'x,y,z'. Defaults to 2048 units along the player's view."
			},
			Handler = Trace
		} );

		Add( new Verb
		{
			Name = "get_limits",
			Description = "Report the server's per-player spawn limits. -1 means unlimited, 0 means none allowed.",
			Handler = GetLimits
		} );
	}

	private static void Add( Verb verb ) => _verbs[verb.Name] = verb;

	public static async Task<JsonNode> InvokeAsync( string name, JsonObject args )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "No verb given" );

		if ( !_verbs.TryGetValue( name, out var verb ) )
			throw new ArgumentException( $"Unknown verb '{name}'. Known verbs: {string.Join( ", ", _verbs.Keys )}" );

		return await verb.Handler( args ?? new JsonObject() );
	}

	// ---- handlers -------------------------------------------------------

	private static Task<JsonNode> SpawnProp( JsonObject args )
	{
		var ident = Str( args, "ident" );

		if ( string.IsNullOrWhiteSpace( ident ) )
			throw new ArgumentException( "'ident' is required" );

		// let agents pass a bare model path without knowing about ident prefixes
		if ( !ident.Contains( ':' ) )
			ident = $"prop:{ident}";

		GameManager.Spawn( ident );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["ident"] = ident,
			["note"] = "Spawn requested. The host places it where the player is looking; call list_props to confirm."
		} );
	}

	private static Task<JsonNode> Undo( JsonObject args )
	{
		// the 'undo' ConCmd is flagged Server, so this reaches the host with the
		// caller attached and undoes that player's own stack
		ConsoleSystem.Run( "undo" );

		return Task.FromResult<JsonNode>( new JsonObject { ["undone"] = true } );
	}

	private static Task<JsonNode> Cleanup( JsonObject args )
	{
		var scope = Str( args, "scope", "mine" ).ToLowerInvariant();

		switch ( scope )
		{
			case "mine":
				CleanupSystem.RpcCleanUpMine();
				break;

			case "all":
				// host-side this checks the caller actually has admin
				CleanupSystem.RpcCleanUpAll();
				break;

			default:
				throw new ArgumentException( $"'scope' must be 'mine' or 'all', got '{scope}'" );
		}

		return Task.FromResult<JsonNode>( new JsonObject { ["scope"] = scope } );
	}

	private static Task<JsonNode> ListProps( JsonObject args )
	{
		var ownerFilter = Str( args, "owner" );
		var limit = Int( args, "limit", 100 );

		var results = new JsonArray();
		var total = 0;

		foreach ( var ownable in Game.ActiveScene.GetAllComponents<Ownable>() )
		{
			if ( !ownable.IsValid() )
				continue;

			var ownerName = ownable.Owner?.DisplayName ?? "world";

			if ( !string.IsNullOrWhiteSpace( ownerFilter ) &&
				 !ownerName.Contains( ownerFilter, StringComparison.OrdinalIgnoreCase ) )
				continue;

			total++;

			if ( results.Count >= limit )
				continue;

			var go = ownable.GameObject;

			results.Add( new JsonObject
			{
				["id"] = go.Id.ToString(),
				["name"] = go.Name,
				["model"] = go.GetComponent<ModelRenderer>()?.Model?.ResourcePath ?? "",
				["position"] = Vec( go.WorldPosition ),
				["owner"] = ownerName
			} );
		}

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["total"] = total,
			["returned"] = results.Count,
			["props"] = results
		} );
	}

	private static Task<JsonNode> Trace( JsonObject args )
	{
		Vector3 from;
		Vector3 to;

		if ( args.ContainsKey( "from" ) || args.ContainsKey( "to" ) )
		{
			if ( !TryVec( Str( args, "from" ), out from ) )
				throw new ArgumentException( "'from' must be 'x,y,z' when tracing explicit points" );

			if ( !TryVec( Str( args, "to" ), out to ) )
				throw new ArgumentException( "'to' must be 'x,y,z' when tracing explicit points" );
		}
		else
		{
			var player = Player.FindForConnection( Connection.Local );

			if ( !player.IsValid() )
				throw new InvalidOperationException( "No local player to trace from" );

			var eyes = player.EyeTransform;
			from = eyes.Position;
			to = eyes.Position + eyes.Forward * 2048f;
		}

		var trace = Game.SceneTrace.Ray( from, to )
			.WithoutTags( "player" )
			.Run();

		var result = new JsonObject
		{
			["hit"] = trace.Hit,
			["from"] = Vec( from ),
			["position"] = Vec( trace.EndPosition ),
			["normal"] = Vec( trace.Normal ),
			["distance"] = MathF.Round( trace.Distance, 2 )
		};

		if ( trace.GameObject.IsValid() )
		{
			result["object"] = trace.GameObject.Name;
			result["objectId"] = trace.GameObject.Id.ToString();
		}

		return Task.FromResult<JsonNode>( result );
	}

	private static Task<JsonNode> GetLimits( JsonObject args )
	{
		return Task.FromResult<JsonNode>( new JsonObject
		{
			["props"] = LimitsSystem.MaxPropsPerPlayer,
			["explosives"] = LimitsSystem.MaxExplosivesPerPlayer,
			["balloons"] = LimitsSystem.MaxBalloons,
			["constraints"] = LimitsSystem.MaxConstraints,
			["emitters"] = LimitsSystem.MaxEmitters,
			["thrusters"] = LimitsSystem.MaxThrusters,
			["hoverballs"] = LimitsSystem.MaxHoverballs,
			["wheels"] = LimitsSystem.MaxWheels
		} );
	}

	// ---- helpers --------------------------------------------------------

	private static string Str( JsonObject args, string key, string fallback = null )
		=> args.TryGetPropertyValue( key, out var node ) && node is not null ? node.ToString() : fallback;

	private static int Int( JsonObject args, string key, int fallback )
		=> args.TryGetPropertyValue( key, out var node ) && node is not null && int.TryParse( node.ToString(), out var v )
			? v
			: fallback;

	/// <summary>Vectors travel as "x,y,z" strings, matching the editor MCP server's convention.</summary>
	private static string Vec( Vector3 v ) => $"{v.x:0.##},{v.y:0.##},{v.z:0.##}";

	private static bool TryVec( string s, out Vector3 v )
	{
		v = default;

		if ( string.IsNullOrWhiteSpace( s ) )
			return false;

		var parts = s.Split( ',', StringSplitOptions.TrimEntries );
		if ( parts.Length != 3 )
			return false;

		if ( !float.TryParse( parts[0], out var x ) ) return false;
		if ( !float.TryParse( parts[1], out var y ) ) return false;
		if ( !float.TryParse( parts[2], out var z ) ) return false;

		v = new Vector3( x, y, z );
		return true;
	}
}
