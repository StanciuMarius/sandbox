using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

/// <summary>
/// The operations an agent is allowed to perform, and nothing else.
///
/// Every verb routes through a command, tool or RPC the game already exposes, so the existing rules
/// still hold: the host stays authoritative, props are attributed to the caller, prop limits and
/// prop protection apply, and everything lands on the player's undo stack. Tool verbs go through
/// <see cref="ToolMode.PerformAction"/>, which raises the same events a real click does - an agent
/// gets no route through a tool that the player doesn't have.
///
/// Most verbs need to be told what to act on. That is what markers are for: the player dots points
/// with the Marker tool at their own pace, and the agent names them - "A", "B" - instead of relying
/// on where the player's camera happens to be pointing at that instant. See <see cref="AgentTargets"/>
/// for the full set of accepted target forms.
///
/// Adding a verb is the deliberate act of widening what an agent can do. Prefer a narrow verb over
/// a general one.
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

	/// <summary>
	/// Shared blurb for every argument that names something to act on, so the accepted forms are
	/// stated once and every verb says the same thing about them.
	/// </summary>
	private const string TargetHelp =
		"What to act on. A marker label like 'A' (best - the player places these with the Marker tool), " +
		"'aim' for wherever the player is currently looking, 'pointer' for the most recent marker, " +
		"an object id from list_objects, or 'at:x,y,z'.";

	/// <summary>
	/// Placement tools, and whether their secondary action is a second way of placing rather than a
	/// setting toggle. Thrusters and emitters can go down without welding; balloons without a rope.
	/// A wheel's secondary flips its axle instead, so there is nothing to offer there.
	/// </summary>
	private static readonly Dictionary<string, bool> PlacementTools = new( StringComparer.OrdinalIgnoreCase )
	{
		["thruster"] = true,
		["emitter"] = true,
		["balloon"] = true,
		["wheel"] = false,
		["hoverball"] = false
	};

	/// <summary>
	/// Rebuild the table from scratch.
	///
	/// Hotload carries the contents of a static field across an assembly swap rather than
	/// re-running the static constructor, so a verb added while the editor is running never
	/// appears - the game keeps serving the table it built at startup. Calling this picks up
	/// the new ones without restarting play mode and losing the session.
	/// </summary>
	[ConCmd( "bridge_reload_verbs", Help = "Rebuild the agent verb table after a code change." )]
	public static void Reload()
	{
		_verbs.Clear();
		Build();

		Log.Info( $"[bridge] verb table rebuilt - {_verbs.Count} verbs" );
	}

	static AgentVerbs()
	{
		Build();
	}

	private static void Build()
	{
		// ---- building ----------------------------------------------------

		Add( new Verb
		{
			Name = "spawn_prop",
			Description = "Spawn a prop. With no 'at' this behaves like the 'spawn' console command and drops it " +
				"where the player is looking; give 'at' to place it on a marker instead and get its id back.",
			Args =
			{
				["ident"] = "What to spawn. Either a bare model path like 'models/dev/box.vmdl', or a prefixed " +
					"ident like 'prop:models/dev/box.vmdl', 'entity:<path>' or 'dupe.local:<id>'.",
				["at"] = "Optional. " + TargetHelp + " Requires the player to be hosting."
			},
			Handler = SpawnProp
		} );

		Add( new Verb
		{
			Name = "place_entity",
			Description = "Place a thruster, wheel, hoverball, balloon or emitter on something, using the game's " +
				"own tool for it. Returns the ids of whatever was created.",
			Args =
			{
				["kind"] = "One of: thruster, wheel, hoverball, balloon, emitter.",
				["target"] = TargetHelp,
				["attach"] = "Default true. False places a thruster or emitter without welding it on, or a " +
					"balloon without its rope. Not available for wheels or hoverballs."
			},
			Handler = PlaceEntity
		} );

		Add( new Verb
		{
			Name = "constrain",
			Description = "Join two points together with a constraint, the same as clicking each in turn with the " +
				"matching tool. The two points must be on different objects (except for rope, which can loop back " +
				"to itself).",
			Args =
			{
				["kind"] = "One of: weld, rope, elastic, slider, ballsocket, nocollide, hydraulic, linker.",
				["a"] = "First point. " + TargetHelp,
				["b"] = "Second point. " + TargetHelp
			},
			Handler = Constrain
		} );

		Add( new Verb
		{
			Name = "keep_upright",
			Description = "Hold an object upright, either anchored to the world or linked to a second object.",
			Args =
			{
				["target"] = TargetHelp,
				["to"] = "Optional second point to stay upright relative to. Without it, anchors to the world."
			},
			Handler = KeepUpright
		} );

		// ---- editing -----------------------------------------------------

		Add( new Verb
		{
			Name = "set_mass",
			Description = "Set an object's mass in kilograms, using the Mass tool.",
			Args =
			{
				["target"] = TargetHelp,
				["value"] = "Mass in kg. Pass 0 to reset it to the model's default."
			},
			Handler = SetMass
		} );

		Add( new Verb
		{
			Name = "set_unbreakable",
			Description = "Make a prop indestructible, or breakable again.",
			Args =
			{
				["target"] = TargetHelp,
				["value"] = "true to make it unbreakable, false to restore its normal health. Default true."
			},
			Handler = SetUnbreakable
		} );

		Add( new Verb
		{
			Name = "remove_object",
			Description = "Delete a spawned object, using the Remover tool. Only works on things tagged removable, " +
				"so the map itself is safe.",
			Args = { ["target"] = TargetHelp },
			Handler = RemoveObject
		} );

		Add( new Verb
		{
			Name = "remove_constraints",
			Description = "Strip constraints of one kind off an object and everything linked to it - the reload " +
				"action of the matching constraint tool.",
			Args =
			{
				["kind"] = "One of: weld, rope, elastic, slider, ballsocket, nocollide, hydraulic, linker, upright.",
				["target"] = TargetHelp
			},
			Handler = RemoveConstraints
		} );

		// ---- tools -------------------------------------------------------

		Add( new Verb
		{
			Name = "list_tools",
			Description = "List every toolgun tool, with the settings each one exposes and their current values. " +
				"Use this to discover what set_tool_option can change.",
			Args = { ["tool"] = "Optional. Limit to one tool by name." },
			Handler = ListTools
		} );

		Add( new Verb
		{
			Name = "set_tool_option",
			Description = "Change one of a tool's settings - rope slack, weld rigidity, which thruster model to " +
				"place, and so on. The setting sticks until changed, so set it before the verb that uses it.",
			Args =
			{
				["tool"] = "Tool name, e.g. 'rope'. See list_tools.",
				["option"] = "Setting name, e.g. 'Slack'. See list_tools.",
				["value"] = "New value. Numbers, true/false, text or an enum name, matching the setting's type."
			},
			Handler = SetToolOption
		} );

		Add( new Verb
		{
			Name = "use_tool",
			Description = "Run any tool's primary, secondary or reload action against a point. The escape hatch " +
				"for tools with no dedicated verb - decal, trail, stacker, resizer - and for the secondary actions " +
				"of ones that do. Check list_tools for what each action does before using it.",
			Args =
			{
				["tool"] = "Tool name. See list_tools.",
				["action"] = "One of: primary, secondary, reload. Default primary.",
				["target"] = TargetHelp
			},
			Handler = UseTool
		} );

		// ---- markers -----------------------------------------------------

		Add( new Verb
		{
			Name = "list_markers",
			Description = "List the points the player has dotted with the Marker tool. These are the handles to " +
				"use for anything positional - ask the player to mark the spots rather than guessing coordinates.",
			Handler = ListMarkers
		} );

		Add( new Verb
		{
			Name = "clear_markers",
			Description = "Remove all of the player's markers.",
			Handler = ClearMarkers
		} );

		// ---- world -------------------------------------------------------

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

	// ---- building handlers ----------------------------------------------

	private static async Task<JsonNode> SpawnProp( JsonObject args )
	{
		var ident = Str( args, "ident" );

		if ( string.IsNullOrWhiteSpace( ident ) )
			throw new ArgumentException( "'ident' is required" );

		// let agents pass a bare model path without knowing about ident prefixes
		if ( !ident.Contains( ':' ) )
			ident = $"prop:{ident}";

		var at = Str( args, "at" );

		if ( string.IsNullOrWhiteSpace( at ) )
		{
			GameManager.Spawn( ident );

			return new JsonObject
			{
				["ident"] = ident,
				["note"] = "Spawn requested. The host places it where the player is looking; call list_props to confirm."
			};
		}

		// placing at a chosen point means spawning host-side and waiting for the object back, which
		// the broadcast path can't do
		if ( !Networking.IsHost )
			throw new InvalidOperationException( "Spawning at a marker needs the player to be hosting the session. Drop the 'at' argument to spawn where they're looking instead." );

		var player = Player.FindLocalPlayer();
		if ( !player.IsValid() )
			throw new InvalidOperationException( "No local player" );

		// borrow the marker tool's trace - it's the least filtered, and this must not switch tools
		var point = AgentTargets.Resolve( at, AgentTools.Get( "marker" ), "at" );

		var objects = await GameManager.SpawnAt( ident, SurfaceTransform( point, player ), player );

		if ( objects is not { Count: > 0 } )
			throw new InvalidOperationException( $"Nothing spawned for '{ident}' - the ident may not resolve, or a spawn limit refused it." );

		var ids = new JsonArray();
		foreach ( var go in objects )
		{
			if ( go.IsValid() )
				ids.Add( go.Id.ToString() );
		}

		return new JsonObject
		{
			["ident"] = ident,
			["spawned"] = ids,
			["position"] = Vec( objects[0].WorldPosition )
		};
	}

	private static Task<JsonNode> PlaceEntity( JsonObject args )
	{
		var kind = Str( args, "kind" );

		if ( string.IsNullOrWhiteSpace( kind ) )
			throw new ArgumentException( $"'kind' is required. One of: {string.Join( ", ", PlacementTools.Keys )}" );

		if ( !PlacementTools.TryGetValue( kind, out var hasAlternatePlacement ) )
			throw new ArgumentException( $"'{kind}' isn't something place_entity can place. One of: {string.Join( ", ", PlacementTools.Keys )}. For other tools use use_tool." );

		var attach = Bool( args, "attach", true );

		if ( !attach && !hasAlternatePlacement )
			throw new ArgumentException( $"A {kind} is always attached to what it's placed on - drop the 'attach' argument." );

		var tool = AgentTools.Activate( kind );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		var input = attach ? ToolInput.Primary : ToolInput.Secondary;

		if ( !tool.PerformAction( input, point ) )
			throw new InvalidOperationException( Refused( kind ) );

		var created = Created( tool );
		RequireCreated( created, kind );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["kind"] = kind,
			["attached"] = attach,
			["created"] = created,
			["position"] = Vec( point.WorldPosition() )
		} );
	}

	private static Task<JsonNode> Constrain( JsonObject args )
	{
		var kind = Str( args, "kind" );

		if ( string.IsNullOrWhiteSpace( kind ) )
			throw new ArgumentException( "'kind' is required. One of: weld, rope, elastic, slider, ballsocket, nocollide, hydraulic, linker." );

		// resolve and check before switching, so a bad kind doesn't leave the player holding it
		if ( AgentTools.Get( kind ) is not BaseConstraintToolMode constraint )
			throw new ArgumentException( $"'{kind}' isn't a constraint tool. One of: weld, rope, elastic, slider, ballsocket, nocollide, hydraulic, linker." );

		var tool = AgentTools.Activate( kind );

		var a = AgentTargets.Resolve( Str( args, "a" ), tool, "a" );
		var b = AgentTargets.Resolve( Str( args, "b" ), tool, "b" );

		if ( !constraint.PerformConstraint( a, b ) )
		{
			if ( a.GameObject == b.GameObject && !constraint.CanConstraintToSelf )
				throw new InvalidOperationException( $"Both points are on the same object, which a {kind} can't join. Mark a point on each of the two objects." );

			throw new InvalidOperationException( Refused( kind ) );
		}

		var created = Created( tool );
		RequireCreated( created, kind );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["kind"] = kind,
			["created"] = created,
			["a"] = Vec( a.WorldPosition() ),
			["b"] = Vec( b.WorldPosition() )
		} );
	}

	private static Task<JsonNode> KeepUpright( JsonObject args )
	{
		var tool = AgentTools.Activate<KeepUprightTool>( "upright" );

		var target = AgentTargets.Resolve( Str( args, "target" ), tool );

		var toSpec = Str( args, "to" );
		ToolMode.SelectionPoint? second = string.IsNullOrWhiteSpace( toSpec )
			? null
			: AgentTargets.Resolve( toSpec, tool, "to" );

		if ( !tool.PerformUpright( target, second ) )
			throw new InvalidOperationException( Refused( "upright" ) );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["anchoredTo"] = second.HasValue ? "object" : "world",
			["created"] = Created( tool )
		} );
	}

	// ---- editing handlers ------------------------------------------------

	private static Task<JsonNode> SetMass( JsonObject args )
	{
		if ( !args.ContainsKey( "value" ) )
			throw new ArgumentException( "'value' is required - the mass in kg, or 0 to reset." );

		var value = Float( args, "value", 100f );

		var tool = AgentTools.Activate<MassTool>( "mass" );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		// the tool reads its own Value when the action fires, so set it first
		tool.Value = MathF.Max( value, 0f );

		var input = value <= 0f ? ToolInput.Reload : ToolInput.Primary;

		if ( !tool.PerformAction( input, point ) )
			throw new InvalidOperationException( "Couldn't set mass - the target may have no rigidbody, so nothing to weigh." );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["mass"] = value <= 0f ? "reset to model default" : $"{value}",
			["target"] = point.GameObject.Id.ToString()
		} );
	}

	private static Task<JsonNode> SetUnbreakable( JsonObject args )
	{
		var value = Bool( args, "value", true );

		var tool = AgentTools.Activate<UnbreakableTool>( "unbreakable" );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		if ( !tool.PerformAction( value ? ToolInput.Primary : ToolInput.Secondary, point ) )
			throw new InvalidOperationException( "Couldn't change that - the target isn't a breakable prop." );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["unbreakable"] = value,
			["target"] = point.GameObject.Id.ToString()
		} );
	}

	private static Task<JsonNode> RemoveObject( JsonObject args )
	{
		var tool = AgentTools.Activate<RemoverTool>( "remover" );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		var id = point.GameObject.Id.ToString();

		if ( !tool.PerformAction( ToolInput.Primary, point ) )
			throw new InvalidOperationException( "Couldn't remove that." );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["removed"] = id,
			["note"] = "The remover only deletes things tagged removable, so a no-op here means it was map geometry."
		} );
	}

	private static Task<JsonNode> RemoveConstraints( JsonObject args )
	{
		var kind = Str( args, "kind" );

		if ( string.IsNullOrWhiteSpace( kind ) )
			throw new ArgumentException( "'kind' is required. One of: weld, rope, elastic, slider, ballsocket, nocollide, hydraulic, linker, upright." );

		if ( AgentTools.Get( kind ) is not (BaseConstraintToolMode or KeepUprightTool) )
			throw new ArgumentException( $"'{kind}' isn't a constraint tool. One of: weld, rope, elastic, slider, ballsocket, nocollide, hydraulic, linker, upright." );

		var tool = AgentTools.Activate( kind );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		var removed = tool switch
		{
			BaseConstraintToolMode constraint => constraint.PerformRemoveConstraints( point.GameObject ),
			KeepUprightTool upright => upright.PerformRemoveUpright( point.GameObject ),
			_ => throw new ArgumentException( $"'{kind}' isn't a constraint tool." )
		};

		if ( !removed )
			throw new InvalidOperationException( Refused( kind ) );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["kind"] = kind,
			["target"] = point.GameObject.Id.ToString()
		} );
	}

	// ---- tool handlers ---------------------------------------------------

	private static Task<JsonNode> ListTools( JsonObject args )
	{
		var filter = Str( args, "tool" );

		var tools = new JsonArray();

		foreach ( var (name, type) in AgentTools.Distinct )
		{
			if ( !string.IsNullOrWhiteSpace( filter ) && !name.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
				continue;

			ToolMode instance;

			try
			{
				instance = AgentTools.Get( name );
			}
			catch ( Exception )
			{
				// the toolgun may not have every component up yet; skip rather than fail the listing
				continue;
			}

			// a tool the player has never selected hasn't declared its actions yet, and their labels
			// are most of what makes this listing worth reading
			instance.EnsureActionsRegistered();

			var options = new JsonArray();

			foreach ( var option in AgentTools.Options( instance ) )
			{
				options.Add( new JsonObject
				{
					["name"] = option.Name,
					["type"] = option.PropertyType?.Name ?? "",
					["value"] = Describe( SafeGet( option, instance ) )
				} );
			}

			tools.Add( new JsonObject
			{
				["name"] = name,
				["title"] = Phrase( type.Title ),
				["kind"] = instance is BaseConstraintToolMode ? "constraint" : "single-point",
				["primary"] = Phrase( instance.PrimaryAction ),
				["secondary"] = Phrase( instance.SecondaryAction ),
				["reload"] = Phrase( instance.ReloadAction ),
				["options"] = options
			} );
		}

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["count"] = tools.Count,
			["tools"] = tools
		} );
	}

	private static Task<JsonNode> SetToolOption( JsonObject args )
	{
		var toolName = Str( args, "tool" );
		var optionName = Str( args, "option" );

		if ( string.IsNullOrWhiteSpace( optionName ) )
			throw new ArgumentException( "'option' is required. Call list_tools to see what a tool exposes." );

		if ( !args.TryGetPropertyValue( "value", out var valueNode ) || valueNode is null )
			throw new ArgumentException( "'value' is required" );

		// don't switch the player's tool just to change a setting on it
		var tool = AgentTools.Get( toolName );

		var option = AgentTools.Options( tool )
			.FirstOrDefault( x => string.Equals( x.Name, optionName, StringComparison.OrdinalIgnoreCase ) );

		if ( option is null )
		{
			var known = string.Join( ", ", AgentTools.Options( tool ).Select( x => x.Name ) );
			throw new ArgumentException( $"'{toolName}' has no setting '{optionName}'. It has: {(string.IsNullOrEmpty( known ) ? "none" : known)}" );
		}

		option.SetValue( tool, Coerce( valueNode, option ) );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["tool"] = toolName,
			["option"] = option.Name,
			["value"] = Describe( SafeGet( option, tool ) )
		} );
	}

	private static Task<JsonNode> UseTool( JsonObject args )
	{
		var toolName = Str( args, "tool" );
		var actionName = Str( args, "action", "primary" ).ToLowerInvariant();

		var input = actionName switch
		{
			"primary" => ToolInput.Primary,
			"secondary" => ToolInput.Secondary,
			"reload" => ToolInput.Reload,
			_ => throw new ArgumentException( $"'action' must be primary, secondary or reload, got '{actionName}'" )
		};

		if ( AgentTools.Get( toolName ) is BaseConstraintToolMode && input != ToolInput.Reload )
			throw new ArgumentException( $"'{toolName}' is a constraint tool and needs two points - use the constrain verb instead." );

		var tool = AgentTools.Activate( toolName );

		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		if ( !tool.PerformAction( input, point ) )
			throw new InvalidOperationException( $"'{toolName}' has no {actionName} action, or {Refused( toolName )}" );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["tool"] = toolName,
			["action"] = actionName,
			["created"] = Created( tool ),
			["position"] = Vec( point.WorldPosition() )
		} );
	}

	// ---- marker handlers -------------------------------------------------

	private static Task<JsonNode> ListMarkers( JsonObject args )
	{
		var system = MarkerSystem.Current;

		var markers = new JsonArray();

		foreach ( var marker in system?.For( Connection.Local ) ?? new List<AgentMarker>() )
		{
			markers.Add( new JsonObject
			{
				["label"] = marker.Label,
				["position"] = Vec( marker.WorldPosition ),
				["on"] = marker.IsWorld ? "world" : marker.Target.Name,
				["objectId"] = marker.Target.Id.ToString()
			} );
		}

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["count"] = markers.Count,
			["markers"] = markers,
			["note"] = markers.Count > 0
				? "Pass a label as a target, e.g. constrain --kind weld --a A --b B."
				: "No markers yet. Ask the player to select the Marker tool (Q menu, under Tools) and click the points you need."
		} );
	}

	private static Task<JsonNode> ClearMarkers( JsonObject args )
	{
		var removed = MarkerSystem.Current?.Clear( Connection.Local ) ?? 0;

		return Task.FromResult<JsonNode>( new JsonObject { ["removed"] = removed } );
	}

	// ---- world handlers --------------------------------------------------

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

		// markers left pointing at deleted objects are just clutter
		MarkerSystem.Current?.Clear( Connection.Local );

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
			if ( !AgentTargets.TryVec( Str( args, "from" ), out from ) )
				throw new ArgumentException( "'from' must be 'x,y,z' when tracing explicit points" );

			if ( !AgentTargets.TryVec( Str( args, "to" ), out to ) )
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

	/// <summary>
	/// A tool action returning false almost always means a limit or an ownership check stopped it,
	/// since the arguments were validated before we got here. Say so, rather than "failed".
	/// </summary>
	private static string Refused( string what ) =>
		$"the {what} action was refused - usually a spawn limit (call get_limits) or prop protection on an object owned by someone else.";

	/// <summary>
	/// Complain when an action that should have built something quietly built nothing.
	/// </summary>
	/// <remarks>
	/// A tool action can run to completion and still do nothing - a missing resource definition, a
	/// surface it won't attach to. Without this the verb reports success with an empty list, and the
	/// agent goes on to reference a thing that was never made.
	///
	/// Only checked on the host, because a tool records what it created inside its host-side RPC:
	/// a client driving the bridge never sees the objects even when the action worked perfectly.
	/// </remarks>
	private static void RequireCreated( JsonArray created, string what )
	{
		if ( created.Count > 0 || !Networking.IsHost )
			return;

		throw new InvalidOperationException( $"The {what} tool ran but produced nothing. The target surface may not accept it, or the tool's configured model may be missing - check 'list_tools --tool {what}'." );
	}

	/// <summary>Ids of whatever the tool's last action created, for the agent to build on.</summary>
	private static JsonArray Created( ToolMode tool )
	{
		var ids = new JsonArray();

		if ( tool.LastCreatedObjects is null )
			return ids;

		foreach ( var go in tool.LastCreatedObjects )
		{
			if ( go.IsValid() )
				ids.Add( go.Id.ToString() );
		}

		return ids;
	}

	/// <summary>
	/// Place a spawn on the surface a selection point sits on, turned to face the player - the same
	/// framing <see cref="GameManager.AimTransform"/> gives a normal spawn.
	/// </summary>
	private static Transform SurfaceTransform( ToolMode.SelectionPoint point, Player player )
	{
		var surface = point.WorldTransform();

		// TraceFromRay stores the hit rotation as LookAt( normal ), so Forward is the surface normal
		var up = surface.Rotation.Forward;
		var backward = -player.EyeTransform.Forward;

		var right = Vector3.Cross( up, backward ).Normal;
		var forward = Vector3.Cross( right, up ).Normal;

		return new Transform( surface.Position, Rotation.LookAt( forward, up ) );
	}

	/// <summary>
	/// Coerce an incoming argument into whatever type a tool setting actually is.
	/// </summary>
	/// <remarks>
	/// Everything arrives as a JSON string, because the CLI has no idea what type any given setting
	/// wants - so "50" has to become a float for Slack and stay a string for a resource path, and
	/// "Up" has to become a StackDirection. Hence the ladder: the specific cases first, then the
	/// engine's own deserialiser, both with and without the quotes, which between them cover
	/// numbers, bools, hex colours and anything with a JSON converter.
	/// </remarks>
	private static object Coerce( JsonNode node, PropertyDescription option )
	{
		var type = option.PropertyType;
		var raw = node.ToString();

		if ( type == typeof( string ) )
			return raw;

		if ( type is { IsEnum: true } )
		{
			if ( Enum.TryParse( type, raw, true, out var parsed ) )
				return parsed;

			throw new ArgumentException( $"'{raw}' isn't a valid {type.Name} for '{option.Name}'. Valid values: {string.Join( ", ", Enum.GetNames( type ) )}" );
		}

		if ( type == typeof( bool ) )
		{
			return raw.ToLowerInvariant() switch
			{
				"true" or "1" or "yes" or "on" => true,
				"false" or "0" or "no" or "off" => false,
				_ => throw new ArgumentException( $"'{raw}' isn't true or false for '{option.Name}'" )
			};
		}

		// unquoted first: an incoming "50" is text, but Slack wants the number 50
		try
		{
			return Sandbox.Json.Deserialize( raw, type );
		}
		catch ( Exception )
		{
			// ignored - try it as written instead
		}

		try
		{
			return Sandbox.Json.Deserialize( node.ToJsonString(), type );
		}
		catch ( Exception e )
		{
			throw new ArgumentException( $"'{raw}' isn't a valid {type?.Name} for '{option.Name}': {e.Message}" );
		}
	}

	private static object SafeGet( PropertyDescription option, ToolMode tool )
	{
		try
		{
			return option.GetValue( tool );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	private static string Describe( object value ) => value?.ToString() ?? "";

	/// <summary>
	/// Tool labels are localisation keys like "#tool.hint.weld.source". Resolve them so an agent
	/// reads what the player reads, not the key.
	/// </summary>
	private static string Phrase( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return "";

		return text.StartsWith( '#' ) ? Game.Language.GetPhrase( text.TrimStart( '#' ) ) : text;
	}

	private static string Str( JsonObject args, string key, string fallback = null )
		=> args.TryGetPropertyValue( key, out var node ) && node is not null ? node.ToString() : fallback;

	private static int Int( JsonObject args, string key, int fallback )
		=> args.TryGetPropertyValue( key, out var node ) && node is not null && int.TryParse( node.ToString(), out var v )
			? v
			: fallback;

	private static float Float( JsonObject args, string key, float fallback )
		=> args.TryGetPropertyValue( key, out var node ) && node is not null && float.TryParse( node.ToString(), out var v )
			? v
			: fallback;

	private static bool Bool( JsonObject args, string key, bool fallback )
	{
		var raw = Str( args, key );

		if ( string.IsNullOrWhiteSpace( raw ) )
			return fallback;

		return raw.ToLowerInvariant() switch
		{
			"true" or "1" or "yes" or "on" => true,
			"false" or "0" or "no" or "off" => false,
			_ => fallback
		};
	}

	/// <summary>Vectors travel as "x,y,z" strings, matching the editor MCP server's convention.</summary>
	private static string Vec( Vector3 v ) => $"{v.x:0.##},{v.y:0.##},{v.z:0.##}";
}
