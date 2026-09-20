using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Theseus.Services.Ipc;

namespace Theseus.Services.Run;

/// <summary>
/// The only place in Theseus that writes the hard target.
///
/// <para>
/// This is a single method wrapping a single assignment, and it exists solely so the Daedalus
/// claim cannot be forgotten. An unclaimed target write does not throw, does not log, and does not
/// fail a test — it just makes Daedalus treat us as the user and hold its movement for four
/// seconds. A bug whose only symptom is "movement feels bad sometimes" is one you find in a
/// dungeon at 2am, so the claim lives in the same call as the write and nowhere else.
/// </para>
/// </summary>
public sealed class TargetService
{
    private readonly ITargetManager _targetManager;
    private readonly DaedalusIpc _daedalus;

    public TargetService(ITargetManager targetManager, DaedalusIpc daedalus)
    {
        _targetManager = targetManager;
        _daedalus = daedalus;
    }

    /// <summary>Sets, or with null clears, the hard target.</summary>
    public void SetTarget(IGameObject? target)
    {
        if (target != null)
            _daedalus.RecordTargetWrite(target.GameObjectId);

        _targetManager.Target = target;
    }

    /// <summary>Current hard target, or null.</summary>
    public IGameObject? Current => _targetManager.Target;
}
