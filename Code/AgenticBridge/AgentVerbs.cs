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

		/// <summary>Settings this verb exposes on the tool it drives. Folded into <see cref="Args"/>.</summary>
		public ToolParam[] Params { get; init; } = [];

		/// <summary>
		/// For a verb that takes a 'kind', the kinds it accepts - their settings are folded into
		/// <see cref="Args"/> too, each labelled with the kinds it applies to.
		/// </summary>
		public string[] Kinds { get; init; } = [];

		public Func<JsonObject, Task<JsonNode>> Handler { get; init; }
	}

	/// <summary>
	/// One setting a verb exposes, mapped onto a property of the tool it drives.
	/// </summary>
	/// <remarks>
	/// Every declared parameter is written on every call - the value passed, or this default. A
	/// verb never inherits whatever the tool happened to be set to last time, so a call is
	/// reproducible on its own terms, which is the only basis an agent can script against. This is
	/// also why the agent's tools don't keep cookies; see <see cref="ToolMode.OnEnabled"/>.
	/// </remarks>
	internal sealed class ToolParam
	{
		/// <summary>The verb argument an agent writes, lowercase.</summary>
		public string Name { get; init; }

		/// <summary>The tool property it sets, when that isn't just <see cref="Name"/>.</summary>
		public string Property { get; init; }

		/// <summary>Written whenever the caller leaves it out. Must already be the property's type.</summary>
		public object Default { get; init; }

		public string Description { get; init; }

		public string PropertyName => Property ?? Name;
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
		"an object id from list_props, or 'at:x,y,z'.";

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
			Name = "find_prop",
			Description = "Search sbox.game for a prop model, for when the player asks for something the map " +
				"doesn't already have. Returns idents that spawn_prop takes as-is. Nothing is downloaded until " +
				"one is actually spawned.",
			Args =
			{
				["query"] = "What to look for, e.g. 'wooden chair'. Backend filters can be mixed in with the " +
					"words - 'tag:medieval', 'sort:popular', 'sort:newest'.",
				["limit"] = "How many results to return. Default 10, maximum 50.",
				["offset"] = "How many results to skip, for paging further into a search. Default 0."
			},
			Handler = FindProp
		} );

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
			Kinds = ["thruster", "wheel", "hoverball", "balloon", "emitter"],
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
			Kinds = ["weld", "rope", "elastic", "slider", "ballsocket", "nocollide", "hydraulic", "linker"],
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
			Params = UprightParams,
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
			Description = "List every toolgun tool with the actions it offers. Settings are arguments on the verb " +
				"that drives each tool, not something set separately - the verb list is where to read them, and " +
				"the values shown here are only whatever the last call happened to leave behind.",
			Args = { ["tool"] = "Optional. Limit to one tool by name." },
			Handler = ListTools
		} );

		Add( new Verb
		{
			Name = "use_tool",
			Description = "Run any tool's primary, secondary or reload action against a point. The escape hatch " +
				"for tools with no dedicated verb - resizer, duplicator - and for the secondary actions " +
				"of ones that do. Check list_tools for what each action does before using it. " +
				"'created' lists new objects and is empty for tools that only modify what is already there, so an " +
				"empty list is not a failure - the action succeeded if no error came back. Do not retry on it.",
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
			Name = "stack",
			Description = "Copy an object in a line - the quickest way to build a wall, a tower or a row of " +
				"pillars without placing each piece. Copies are spaced by the object's own size, so they sit " +
				"flush unless you ask for a gap.",
			Args =
			{
				["target"] = TargetHelp
			},
			Params = StackParams,
			Handler = Stack
		} );

		Add( new Verb
		{
			Name = "trail",
			Description = "Give an object a trail that draws a line behind it as it moves. Adds a component to " +
				"the object rather than creating anything, so 'created' is empty on success.",
			Args =
			{
				["target"] = TargetHelp,
				["remove"] = "Set true to take the trail off the object instead of adding one. The settings are ignored."
			},
			Params = TrailParams,
			Handler = Trail
		} );

		Add( new Verb
		{
			Name = "decal",
			Description = "Stick a decal onto a surface - a scorch mark, a sign, a splatter. It parents to " +
				"whatever it lands on, so it moves with that object.",
			Args =
			{
				["target"] = TargetHelp
			},
			Params = DecalParams,
			Handler = Decal
		} );

		Add( new Verb
		{
			Name = "companion",
			Description = "The agent's own body in the world: a second character carrying its own toolgun, so " +
				"driving a tool never takes the one the player is holding, and never changes their tool settings. " +
				"It is summoned automatically by the first verb that needs it and walks itself round to whatever " +
				"it is working on. Call with no arguments to find out where it is.",
			Args =
			{
				["action"] = "'status' (the default), 'summon' to bring it back to the player, or 'dismiss' to send it away."
			},
			Handler = Companion
		} );

		Add( new Verb
		{
			Name = "get_limits",
			Description = "Report the server's per-player spawn limits. -1 means unlimited, 0 means none allowed. " +
				"These are shared with the player - the companion is a second body, not a second budget.",
			Handler = GetLimits
		} );
	}

	private static void Add( Verb verb )
	{
		// so an agent reading the verb list sees the settings alongside the other arguments, with
		// what it gets if it leaves one out
		foreach ( var param in verb.Params )
			verb.Args[param.Name] = $"{param.Description} Optional, defaults to {Describe( param.Default )}.";

		DescribeKindParams( verb );

		_verbs[verb.Name] = verb;
	}

	/// <summary>
	/// Fold the settings of every kind a verb accepts into its argument list, each labelled with
	/// the kinds it applies to.
	/// </summary>
	/// <remarks>
	/// A rope's slack and a weld's easy mode arrive through the same verb but belong to different
	/// kinds, and passing one to the other is a mistake worth being able to see coming. Where
	/// several kinds share an argument name and the same default it is stated once; where the
	/// default varies - every kind has its own 'definition' - it says so instead.
	/// </remarks>
	private static void DescribeKindParams( Verb verb )
	{
		var byName = verb.Kinds
			.SelectMany( kind => ParamsFor( kind ).Select( param => (kind, param) ) )
			.GroupBy( x => x.param.Name );

		foreach ( var group in byName )
		{
			var owners = string.Join( ", ", group.Select( x => x.kind ) );
			var first = group.First().param;

			var defaults = group.Select( x => Describe( x.param.Default ) ).Distinct().ToArray();

			var fallback = defaults.Length == 1
				? $"defaults to {defaults[0]}"
				: "each kind has its own default";

			verb.Args[first.Name] = $"({owners}) {first.Description} Optional, {fallback}.";
		}
	}

	/// <summary>
	/// Activate the tool a verb drives and write every setting the verb declares.
	/// </summary>
	/// <remarks>
	/// Writes all of them, not just the ones passed, so nothing an earlier call did can change what
	/// this one does.
	/// </remarks>
	private static ToolMode Prepare( string toolName, ToolParam[] parameters, JsonObject args )
	{
		var tool = AgentTools.Activate( toolName );

		foreach ( var param in parameters )
		{
			var option = AgentTools.Options( tool )
				.FirstOrDefault( x => string.Equals( x.Name, param.PropertyName, StringComparison.Ordinal ) );

			// a rename in the tool rather than anything the caller did
			if ( option is null )
				throw new InvalidOperationException( $"'{toolName}' has no '{param.PropertyName}' to back the '{param.Name}' argument." );

			var passed = args.TryGetPropertyValue( param.Name, out var node ) && node is not null;

			option.SetValue( tool, passed ? Coerce( node, option ) : param.Default );
		}

		return tool;
	}

	public static async Task<JsonNode> InvokeAsync( string name, JsonObject args )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "No verb given" );

		if ( !_verbs.TryGetValue( name, out var verb ) )
			throw new ArgumentException( $"Unknown verb '{name}'. Known verbs: {string.Join( ", ", _verbs.Keys )}" );

		return await verb.Handler( args ?? new JsonObject() );
	}

	// ---- building handlers ----------------------------------------------

	/// <summary>
	/// Search the backend for models the player could spawn.
	/// </summary>
	/// <remarks>
	/// Narrowed to type:model on the way out, because everything sbox.game hosts is a package -
	/// gamemodes, maps and libraries included - and an ident for one of those is something spawn_prop
	/// can only fail on. An explicit type: in the query leaves ours off, so a deliberate search for
	/// something else is still open.
	///
	/// Nothing is downloaded here. An ident comes back as text, and the model is only fetched if the
	/// agent goes on to spawn it - the cloud path <see cref="PropSpawner"/> already takes for any
	/// ident that isn't a local .vmdl.
	/// </remarks>
	private static async Task<JsonNode> FindProp( JsonObject args )
	{
		var query = Str( args, "query" )?.Trim();

		if ( string.IsNullOrWhiteSpace( query ) )
			throw new ArgumentException( "'query' is required - what to search for, e.g. 'wooden chair'." );

		if ( !query.Contains( "type:", StringComparison.OrdinalIgnoreCase ) )
			query += " type:model";

		// an agent naming a thing wants the good version of it, not the most recent upload
		if ( !query.Contains( "sort:", StringComparison.OrdinalIgnoreCase ) )
			query += " sort:popular";

		var limit = Math.Clamp( Int( args, "limit", 10 ), 1, 50 );
		var offset = Math.Max( Int( args, "offset", 0 ), 0 );

		var found = await Package.FindAsync( query, limit, offset );

		// a null result is the backend not answering, which is worth saying out loud - an agent told
		// "no results" would go and try a different wording of a search that never actually ran
		if ( found is null )
			throw new InvalidOperationException( "sbox.game didn't answer - the player may be offline. The search didn't run, so this isn't 'nothing matched'." );

		var results = new JsonArray();

		foreach ( var package in found.Packages ?? [] )
		{
			var entry = new JsonObject
			{
				["ident"] = package.FullIdent,
				["title"] = package.Title,
				["by"] = package.Org?.Title ?? "",
				["votes"] = package.VotesUp
			};

			if ( !string.IsNullOrWhiteSpace( package.Summary ) )
				entry["summary"] = package.Summary;

			results.Add( entry );
		}

		return new JsonObject
		{
			["query"] = query,
			["total"] = found.TotalCount,
			["returned"] = results.Count,
			["results"] = results,
			["note"] = "Pass an ident straight to spawn_prop - the model downloads on first spawn."
		};
	}

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

		// This path doesn't go through a tool, so pose the companion by hand - otherwise props
		// appear at a marker while it stands somewhere else entirely.
		AgentPawn.ForLocalPlayer().PoseAt( point.WorldPosition() );

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

		var tool = Prepare( kind, ParamsFor( kind ), args );
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

		var tool = Prepare( kind, ParamsFor( kind ), args );

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
		var tool = (KeepUprightTool)Prepare( "upright", UprightParams, args );

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

		// These carry settings, and use_tool would run them on whatever the last call left set -
		// the very thing their own verbs exist to remove.
		if ( ParametricTools.TryGetValue( toolName ?? "", out var verbName ) )
			throw new ArgumentException( $"'{toolName}' has settings, so it has its own verb - call {verbName} instead, passing them as arguments." );

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

	// ---- tools with settings, each driven by one verb ------------------

	/// <summary>
	/// Settings for the tools behind the verbs that take a 'kind', keyed by that kind.
	/// </summary>
	/// <remarks>
	/// Kept per kind rather than per verb because a rope and a weld share a verb but not their
	/// settings. A kind absent from here has nothing to tune.
	/// </remarks>
	private static readonly Dictionary<string, ToolParam[]> KindParams = new( StringComparer.OrdinalIgnoreCase )
	{
		["weld"] =
		[
			new() { Name = "easymode", Property = "EasyMode", Default = true,
				Description = "Whether welding moves the first object so the two marked points touch. True is usually what you want when assembling something; false leaves both where they are." },
			new() { Name = "rigid", Property = "Rigid", Default = false,
				Description = "Whether the joint is completely rigid rather than slightly springy." }
		],
		["rope"] =
		[
			new() { Name = "slack", Property = "Slack", Default = 0f,
				Description = "Extra length beyond the distance between the two points, in units. Zero pulls taut." },
			new() { Name = "rigid", Property = "Rigid", Default = false,
				Description = "Whether the rope resists being compressed as well as stretched." },
			new() { Name = "radius", Property = "Radius", Default = 1f,
				Description = "How thick the rope is drawn, in units." }
		],
		["elastic"] =
		[
			new() { Name = "frequency", Property = "Frequency", Default = 2f,
				Description = "Springiness in hertz. Higher is stiffer and snappier." },
			new() { Name = "damping", Property = "Damping", Default = 0.1f,
				Description = "How quickly the bounce dies away, 0 to 1. Low values wobble for longer." },
			new() { Name = "stretchonly", Property = "StretchOnly", Default = false,
				Description = "Whether it only pulls, never pushes - a bungee rather than a spring." }
		],
		["hydraulic"] =
		[
			new() { Name = "balljoints", Property = "BallJoints", Default = false,
				Description = "Whether the ends pivot freely instead of holding their angle." }
		],
		["ballsocket"] =
		[
			new() { Name = "collision", Property = "EnableCollision", Default = false,
				Description = "Whether the two joined objects still collide with each other." }
		],

		["thruster"] =
		[
			new() { Name = "definition", Property = "Definition", Default = "entities/thruster/basic.tdef",
				Description = "Which variant to place, as a resource path. Each kind has a basic one it uses by default." }
		],
		["wheel"] =
		[
			new() { Name = "definition", Property = "Definition", Default = "entities/wheel/basic.wdef",
				Description = "Which variant to place, as a resource path. Each kind has a basic one it uses by default." }
		],
		["hoverball"] =
		[
			new() { Name = "definition", Property = "Definition", Default = "entities/hoverball/basic.hdef",
				Description = "Which variant to place, as a resource path. Each kind has a basic one it uses by default." }
		],
		["balloon"] =
		[
			new() { Name = "definition", Property = "Definition", Default = "entities/balloon/basic.bdef",
				Description = "Which variant to place, as a resource path. Each kind has a basic one it uses by default." },
			new() { Name = "length", Property = "Length", Default = 50f,
				Description = "Length of the rope tethering it, in units." },
			new() { Name = "force", Property = "Force", Default = 1f,
				Description = "How hard it pulls upward." },
			new() { Name = "rigid", Property = "Rigid", Default = false,
				Description = "Whether the tether is rigid rather than rope-like." },
			new() { Name = "tint", Property = "Tint", Default = Color.White,
				Description = "Balloon colour, as 'r,g,b' or 'r,g,b,a' from 0 to 1." }
		],
		["emitter"] =
		[
			new() { Name = "base", Property = "BaseDef", Default = "entities/emitter/basic.smemit",
				Description = "The emitter body to place, as a resource path." },
			new() { Name = "effect", Property = "EffectDef", Default = "entities/particles/sparks.semit",
				Description = "The effect it gives off, as a resource path." }
		]
	};

	/// <summary>Settings for a kind, or none if it has nothing to tune.</summary>
	private static ToolParam[] ParamsFor( string kind ) =>
		KindParams.TryGetValue( kind ?? "", out var found ) ? found : [];

	private static readonly ToolParam[] UprightParams =
	{
		new() { Name = "hertz", Property = "Hertz", Default = 2f,
			Description = "How stiffly it rights itself, in hertz. Higher snaps upright harder." },
		new() { Name = "damping", Property = "DampingRatio", Default = 0.7f,
			Description = "How much the righting motion is damped, 0 to 1. Low values overshoot and wobble." },
		new() { Name = "torque", Property = "TorqueMultiplier", Default = 5000f,
			Description = "Maximum turning force available. Heavy contraptions need more." }
	};

	/// <summary>Tool name to the verb that drives it, for tools whose settings are verb arguments.</summary>
	private static readonly Dictionary<string, string> ParametricTools = new( StringComparer.OrdinalIgnoreCase )
	{
		["stacker"] = "stack",
		["trail"] = "trail",
		["decal"] = "decal"
	};

	private static readonly ToolParam[] StackParams =
	{
		new() { Name = "count", Property = "StackCount", Default = 1f,
			Description = "How many copies to make, 1 to 50." },
		new() { Name = "direction", Property = "Direction", Default = StackDirection.Up,
			Description = "Which way to stack: Up, Down, Left, Right, Forward or Back." },
		new() { Name = "align", Property = "AlignMode", Default = StackAlignMode.Object,
			Description = "Object to stack along the target's own axes, World to use world axes regardless of how it is turned." },
		new() { Name = "gap", Property = "PositionOffset", Default = 0f,
			Description = "Extra space between copies in units, on top of the object's own size." },
		new() { Name = "anglex", Property = "AngleOffsetX", Default = 0f,
			Description = "Degrees to turn each copy relative to the one before it, around the first axis across the stack. Non-zero bends the stack into an arc." },
		new() { Name = "angley", Property = "AngleOffsetY", Default = 0f,
			Description = "As anglex, around the second across-stack axis." },
		new() { Name = "freeze", Property = "FreezeAll", Default = true,
			Description = "Whether the copies are frozen in place. Leave true while building or the stack collapses." }
	};

	private static readonly ToolParam[] TrailParams =
	{
		new() { Name = "line", Property = "Definition", Default = "entities/trails/basic.ldef",
			Description = "Line definition resource path." },
		new() { Name = "color", Property = "TrailColor", Default = Color.White,
			Description = "Trail colour, as 'r,g,b' or 'r,g,b,a' from 0 to 1." },
		new() { Name = "startwidth", Property = "StartWidth", Default = 4f,
			Description = "Width in units where the trail leaves the object, 0.1 to 128." },
		new() { Name = "endwidth", Property = "EndWidth", Default = 0f,
			Description = "Width at the tail end, 0 to 128. Zero tapers it to a point." },
		new() { Name = "lifetime", Property = "Lifetime", Default = 1f,
			Description = "Seconds a piece of trail lingers, 0.1 to 10." },
		new() { Name = "shadows", Property = "CastShadows", Default = false,
			Description = "Whether the trail casts shadows." }
	};

	private static readonly ToolParam[] DecalParams =
	{
		new() { Name = "decal", Property = "Decal", Default = "",
			Description = "Decal resource path. Required - there is no sensible default." }
	};

	private static Task<JsonNode> Stack( JsonObject args )
	{
		var tool = Prepare( "stacker", StackParams, args );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		if ( !tool.PerformAction( ToolInput.Primary, point ) )
			throw new InvalidOperationException( Refused( "stack" ) );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["created"] = Created( tool ),
			["position"] = Vec( point.WorldPosition() )
		} );
	}

	private static Task<JsonNode> Trail( JsonObject args )
	{
		var removing = string.Equals( Str( args, "remove", "false" ), "true", StringComparison.OrdinalIgnoreCase );

		var tool = Prepare( "trail", TrailParams, args );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		var input = removing ? ToolInput.Secondary : ToolInput.Primary;

		if ( !tool.PerformAction( input, point ) )
			throw new InvalidOperationException( Refused( "trail" ) );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["removed"] = removing,
			["target"] = point.GameObject?.Id.ToString(),
			["position"] = Vec( point.WorldPosition() )
		} );
	}

	private static Task<JsonNode> Decal( JsonObject args )
	{
		if ( string.IsNullOrWhiteSpace( Str( args, "decal" ) ) )
			throw new ArgumentException( "'decal' is required - name the decal resource to place." );

		var tool = Prepare( "decal", DecalParams, args );
		var point = AgentTargets.Resolve( Str( args, "target" ), tool );

		if ( !tool.PerformAction( ToolInput.Primary, point ) )
			throw new InvalidOperationException( Refused( "decal" ) );

		return Task.FromResult<JsonNode>( new JsonObject
		{
			["created"] = Created( tool ),
			["position"] = Vec( point.WorldPosition() )
		} );
	}

	private static Task<JsonNode> Companion( JsonObject args )
	{
		var action = Str( args, "action", "status" ).Trim().ToLowerInvariant();

		switch ( action )
		{
			case "dismiss":
				AgentPawn.DespawnForLocalPlayer();
				return Task.FromResult<JsonNode>( new JsonObject { ["present"] = false } );

			case "summon":
			{
				var pawn = AgentPawn.ForLocalPlayer();
				pawn.ReturnToOwner();
				return Task.FromResult<JsonNode>( DescribeCompanion( pawn ) );
			}

			case "status":
			{
				// deliberately doesn't summon - asking where it is shouldn't conjure it
				var pawn = AgentPawn.Find( Connection.Local );

				return Task.FromResult<JsonNode>( pawn.IsValid()
					? DescribeCompanion( pawn )
					: new JsonObject { ["present"] = false } );
			}

			default:
				throw new ArgumentException( $"Unknown action '{action}'. Use 'status', 'summon' or 'dismiss'." );
		}
	}

	private static JsonObject DescribeCompanion( AgentPawn pawn ) => new()
	{
		["present"] = true,
		["name"] = pawn.Player?.DisplayName,
		["position"] = Vec( pawn.WorldPosition ),
		["holding"] = pawn.Toolgun?.GetCurrentMode()?.Name
	};

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

		// Before the Json fallback, which takes "1,0.2,0.2" and hands back a transparent black
		// rather than failing - a wrong colour with no error is worse than a rejected one.
		if ( type == typeof( Color ) )
		{
			if ( Color.Parse( raw ) is not { } color )
				throw new ArgumentException( $"'{raw}' isn't a colour for '{option.Name}'. Expected 'r,g,b' or 'r,g,b,a', each 0 to 1." );

			// Parse leaves alpha at zero when it isn't given, which would make the thing invisible.
			// Someone writing "1,0.2,0.2" means opaque red.
			if ( raw.Count( x => x == ',' ) < 3 )
				color.a = 1f;

			return color;
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
