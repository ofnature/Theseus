using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;

namespace Theseus.Services.Duty;

/// <summary>Where a duty run stands.</summary>
public enum DutyPhase
{
    /// <summary>Not inside instanced content.</summary>
    Outside,

    /// <summary>Zoned in, barrier still up — the countdown before the duty actually starts.</summary>
    Waiting,

    /// <summary>The duty is running.</summary>
    InProgress,

    /// <summary>Cleared, still inside. The window in which chores and the exit happen.</summary>
    Completed,
}

/// <summary>
/// Duty start / complete / wipe, plus the instance identity everything else keys off.
///
/// <para>
/// The transitions themselves come from Dalamud's <see cref="IDutyState"/> rather than being
/// re-detected here — it already hooks the duty director's own events, and a second hand-rolled
/// detector would only be a second thing to be wrong. What this adds is the part Dalamud does not
/// model: which *instance* is running (<see cref="DutyRunKey"/>), so a checkpoint written before a
/// crash can be matched against the duty that is live now, and the enter/leave edges that bracket
/// a run.
/// </para>
///
/// <para>
/// Note that <see cref="IDutyState.DutyWiped"/> is a whole-party duty wipe. It is not the
/// boss-level wipe-vs-clear question, which needs the objective delta — see
/// <see cref="EncounterClassifier"/>.
/// </para>
/// </summary>
public sealed class DutyLifecycle : IDisposable
{
    private readonly IDutyState _dutyState;
    private readonly ICondition _condition;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;

    public DutyLifecycle(
        IDutyState dutyState,
        ICondition condition,
        IClientState clientState,
        IFramework framework)
    {
        _dutyState = dutyState;
        _condition = condition;
        _clientState = clientState;
        _framework = framework;

        _dutyState.DutyStarted += OnDutyStarted;
        _dutyState.DutyCompleted += OnDutyCompleted;
        _dutyState.DutyWiped += OnDutyWiped;
        _dutyState.DutyRecommenced += OnDutyRecommenced;
        _framework.Update += OnUpdate;
    }

    /// <summary>Current phase. Only ever changed on the framework thread.</summary>
    public DutyPhase Phase { get; private set; } = DutyPhase.Outside;

    /// <summary>Identity of the running instance, or <see cref="DutyRunKey.None"/> outside one.</summary>
    public DutyRunKey RunKey { get; private set; } = DutyRunKey.None;

    /// <summary>Inside instanced content, whether or not the duty has started.</summary>
    public bool IsInDuty => Phase != DutyPhase.Outside;

    /// <summary>A new instance became identifiable — a fresh run, or a re-queue of the same duty.</summary>
    public event Action<DutyRunKey>? RunEntered;

    /// <summary>The barrier dropped.</summary>
    public event Action<DutyRunKey>? DutyStarted;

    /// <summary>The duty was cleared.</summary>
    public event Action<DutyRunKey>? DutyCompleted;

    /// <summary>The party wiped and the duty reset.</summary>
    public event Action<DutyRunKey>? DutyWiped;

    /// <summary>Left the instance, by any route including a disconnect.</summary>
    public event Action<DutyRunKey>? RunLeft;

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _dutyState.DutyStarted -= OnDutyStarted;
        _dutyState.DutyCompleted -= OnDutyCompleted;
        _dutyState.DutyWiped -= OnDutyWiped;
        _dutyState.DutyRecommenced -= OnDutyRecommenced;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!IsBoundByDuty())
        {
            if (Phase != DutyPhase.Outside)
                LeaveRun();
            return;
        }

        var (contentId, start) = DirectorAccess.CurrentRunIdentity();
        var key = new DutyRunKey(_clientState.TerritoryType, contentId, start);

        // The director exists for a few frames before it is populated. An unidentifiable instance
        // is "not yet", not "a different run" — minting a key from zeroes would make every entry
        // look like a re-queue and throw away every resume.
        if (!key.IsValid || key == RunKey)
            return;

        RunKey = key;
        Phase = _dutyState.IsDutyStarted ? DutyPhase.InProgress : DutyPhase.Waiting;
        RunEntered?.Invoke(key);
    }

    private bool IsBoundByDuty()
        => _condition[ConditionFlag.BoundByDuty]
           || _condition[ConditionFlag.BoundByDuty56]
           || _condition[ConditionFlag.BoundByDuty95];

    private void LeaveRun()
    {
        var key = RunKey;
        Phase = DutyPhase.Outside;
        RunKey = DutyRunKey.None;
        RunLeft?.Invoke(key);
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        Phase = DutyPhase.InProgress;
        DutyStarted?.Invoke(RunKey);
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        Phase = DutyPhase.Completed;
        DutyCompleted?.Invoke(RunKey);
    }

    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        // Still the same instance — the director does not restart, so the run key is unchanged and
        // a resume after a wipe is still a resume into the same run.
        Phase = DutyPhase.Waiting;
        DutyWiped?.Invoke(RunKey);
    }

    private void OnDutyRecommenced(IDutyStateEventArgs args)
    {
        Phase = DutyPhase.InProgress;
        DutyStarted?.Invoke(RunKey);
    }
}
