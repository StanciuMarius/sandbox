using Sandbox.Rendering;

public partial class Toolgun : ScreenWeapon
{
	public Toolgun()
	{
		// Tools don't use ammo (the engine default is on, which would show an ammo indicator).
		UsesAmmo = false;
	}

	public override void OnCameraMove( Player player, ref Angles angles )
	{
		base.OnCameraMove( player, ref angles );

		var currentMode = GetCurrentMode();
		if ( currentMode is { AbsorbMouseInput: true } )
		{
			angles = default;
		}

		currentMode?.OnCameraMove( player, ref angles );
	}

	protected override void OnAdded( Sandbox.BaseInventoryComponent inventory )
	{
		base.OnAdded( inventory );

		CreateToolComponents();
	}

	public void CreateToolComponents()
	{
		if ( !Networking.IsHost )
		{
			Log.Warning( "CreateToolComponents should be called on the host" );
			return;
		}

		bool enabled = true;

		foreach ( var mode in Game.TypeLibrary.GetTypes<ToolMode>() )
		{
			if ( mode.IsAbstract ) continue;

			Components.Create( mode, enabled );
			enabled = false;
		}

		Network.Refresh( GameObject );
	}

	protected override void OnControl()
	{
		var currentMode = GetCurrentMode();
		if ( currentMode == null )
			return;

		currentMode.OnControl();

		UpdateViewmodelScreen();

		ApplyCoilSpin();
	}

	public override void DrawHud( HudPainter painter, Vector2 crosshair )
	{
		var currentMode = GetCurrentMode();
		currentMode?.DrawHud( painter, crosshair );
	}

	public ToolMode GetCurrentMode() => GetComponent<ToolMode>();

	/// <summary>
	/// Switch to a mode straight away, without the RPC round trip <see cref="SetToolMode"/> takes.
	/// </summary>
	/// <remarks>
	/// The agent bridge needs the mode live in the same frame it acts on it, and a mode that has
	/// never been enabled is not somewhere to be firing RPCs from. Switching for real - rather than
	/// quietly reaching into a disabled component - also means the player can see which tool the
	/// agent is using, on the toolgun screen like any other switch.
	/// </remarks>
	public ToolMode ActivateMode( ToolMode mode )
	{
		if ( !mode.IsValid() ) return null;

		var current = GetCurrentMode();
		if ( current == mode ) return mode;

		if ( current.IsValid() )
			current.Enabled = false;

		mode.Enabled = true;
		GameObject.Enabled = true;

		if ( Networking.IsHost )
			Network.Refresh( GameObject );

		return mode;
	}

	public T GetMode<T>() where T : ToolMode
	{
		return GetComponent<T>( true );
	}

	[Rpc.Host]
	public void SetToolMode( string name )
	{
		var targetMode = Game.TypeLibrary.GetType<ToolMode>( name );
		if ( targetMode == null )
		{
			Log.Warning( $"Unknown Mode {name}" );
			return;
		}

		var newMode = GetComponents<ToolMode>( true ).Where( x => x.GetType() == targetMode.TargetType ).FirstOrDefault();
		if ( newMode == null )
		{
			Log.Warning( $"Toolgun missing mode component for {name}" );
			return;
		}

		var currentMode = GetCurrentMode();

		if ( newMode == currentMode )
			return;

		currentMode?.Enabled = false;
		newMode.Enabled = true;

		GameObject.Enabled = true;
		Network.Refresh( GameObject );

		using ( Rpc.FilterInclude( Rpc.Caller ) )
		{
			BroadcastSwitchToolMode();
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void BroadcastSwitchToolMode()
	{
		SwitchToolMode();
	}
}
